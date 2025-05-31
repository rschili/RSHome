using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Responses;
using RSMatrix.Http;

namespace RSHome.Services;

public class OpenAIService
{
    public IConfigService Config { get; private init; }
    public OpenAIResponseClient Client { get; private init; }
    public ILogger Logger { get; private init; }

    public IToolService ToolService { get; private init; }

    public LeakyBucketRateLimiter RateLimiter { get; private init; } = new(10, 60);

    public OpenAIService(IConfigService config, ILogger<OpenAIService> logger, IToolService toolService)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Client = new OpenAIResponseClient(model: "gpt-4.1", apiKey: config.OpenAiApiKey); //  /*"o1" "o3-mini" "gpt-4o"*/
        ToolService = toolService ?? throw new ArgumentNullException(nameof(toolService));
    }
    
    private const string WEATHER_TOOL_NAME = "weather_current";
    private static readonly ResponseTool weatherCurrentTool = ResponseTool.CreateFunctionTool
    (
        functionName: WEATHER_TOOL_NAME,
        functionDescription: "Get the current weather and sunrise/sunset for a given location",
        functionParameters: BinaryData.FromBytes("""
            {
                "type": "object",
                "properties": {
                    "location": {
                        "type": "string",
                        "description": "City name or ZIP code to get the current weather for. When providing a City name, an ISO 3166 country code can be appended with a comma e.g. 'Heidelberg,DE' to avoid ambiguity."
                    }
                },
                "required": [ "location" ]
            }
            """u8.ToArray())
    );

    private const string FORECAST_TOOL_NAME = "weather_forecast";
    private static readonly ResponseTool weatherForecastTool = ResponseTool.CreateFunctionTool
    (
        functionName: FORECAST_TOOL_NAME,
        functionDescription: "Get a 5 day weather forecast for a given location",
        functionParameters: BinaryData.FromBytes("""
            {
                "type": "object",
                "properties": {
                    "location": {
                        "type": "string",
                        "description": "City name or ZIP code to get the current weather for. When providing a City name, an ISO 3166 country code can be appended with a comma e.g. 'Heidelberg,DE' to avoid ambiguity."
                    }
                },
                "required": [ "location" ]
            }
            """u8.ToArray())
    );

    private const string HEISE_TOOL_NAME = "heise_headlines";
    private static readonly ResponseTool heiseHeadlinesTool = ResponseTool.CreateFunctionTool
    (
        functionName: HEISE_TOOL_NAME,
        functionDescription: "Get the latest headlines from Heise Online (Technology related news)",
        functionParameters: BinaryData.FromBytes("""
            {}
            """u8.ToArray())
    );

    private const string POSTILLON_TOOL_NAME = "postillon_headlines";
    private static readonly ResponseTool postillonHeadlinesTool = ResponseTool.CreateFunctionTool
    (
        functionName: POSTILLON_TOOL_NAME,
        functionDescription: "Get the latest headlines from 'Der Postillon', a german satire magazine",
        functionParameters: BinaryData.FromBytes("""
            {}
            """u8.ToArray())
    );

    private static readonly ResponseCreationOptions DefaultOptions = new()
    {
        MaxOutputTokenCount = 1000,
        StoredOutputEnabled = false,
        TextOptions = new ResponseTextOptions
        {
            TextFormat = ResponseTextFormat.CreateTextFormat()
        },
        Tools = { weatherCurrentTool, weatherForecastTool, heiseHeadlinesTool, postillonHeadlinesTool },
        ToolChoice = ResponseToolChoice.CreateAutoChoice(),
    };

    internal static readonly ResponseCreationOptions StructuredJsonArrayOptions = new()
    {
        MaxOutputTokenCount = 1000,
        StoredOutputEnabled = false,
        TextOptions = new ResponseTextOptions
        {
            TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("structured_array",
            BinaryData.FromBytes("""
            {
                "type": "object",
                "properties": {
                    "values": {
                        "type": "array",
                        "items": {
                            "type": "string"
                        }
                    }
                },
                "required": [ "values" ],
                "additionalProperties": false
            }
            """u8.ToArray()), null, true)
        },
        Tools = { weatherCurrentTool, weatherForecastTool, heiseHeadlinesTool, postillonHeadlinesTool },
        ToolChoice = ResponseToolChoice.CreateAutoChoice(),
    };

    public async Task<string?> GenerateResponseAsync(string systemPrompt, IEnumerable<AIMessage> inputs, ResponseCreationOptions? options = null)
    {
        if (!RateLimiter.Leak())
            return null;

        systemPrompt = $"""
            {systemPrompt}
            Aktuell ist [{DateTime.Now.ToLongDateString()}, {DateTime.Now.ToLongTimeString()} Uhr. 
            """;

        var instructions = new List<ResponseItem>() { ResponseItem.CreateDeveloperMessageItem(systemPrompt) };
        foreach (var input in inputs)
        {
            var participantName = input.ParticipantName;
            if (participantName == null || participantName.Length >= 100)
                throw new ArgumentException("Participant name is too long.", nameof(participantName));

            if (!IsValidName(participantName))
                throw new ArgumentException("Participant name is invalid.", nameof(participantName));

            if (input.IsSelf)
            {
                var message = ResponseItem.CreateAssistantMessageItem(input.Message);
                instructions.Add(message);
            }
            else
            {
                var message = ResponseItem.CreateUserMessageItem($"[[{input.ParticipantName}]] {input.Message}");
                instructions.Add(message);
            }
        }

        try
        {
            return await CreateResponseAsync(instructions, 1, 0, options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred during the OpenAI call.");
            return $"Fehler bei der Kommunikation mit OpenAI: {ex.Message}";
        }
    }

    public async Task<string> CreateResponseAsync(List<ResponseItem> instructions, int depth = 1, int toolCalls = 0, ResponseCreationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(instructions, nameof(instructions));
        if (depth > 3)
        {
            Logger.LogWarning("OpenAI call reached maximum recursion depth of 5. Returning empty response.");
            return "Maximale Rekursionstiefe erreicht. Keine Antwort generiert.";
        }

        var result = await Client.CreateResponseAsync(instructions, options ?? OpenAIService.DefaultOptions).ConfigureAwait(false);
        var response = result.Value;
        List<FunctionCallResponseItem> functionCalls = [.. response.OutputItems.Where(item => item is FunctionCallResponseItem).Cast<FunctionCallResponseItem>()];

        if (functionCalls.Count > 0)
        {
            Logger.LogInformation("OpenAI call requested function calls. Depth: {Depth}.", depth);
            foreach (var functionCall in functionCalls)
            {
                Logger.LogInformation("Function call requested: {FunctionName} with arguments: {Arguments}", functionCall.FunctionName, functionCall.FunctionArguments);
                instructions.Add(functionCall);
                toolCalls++;
                await HandleFunctionCall(functionCall, instructions);
            }
            return await CreateResponseAsync(instructions, depth + 1, toolCalls).ConfigureAwait(false);
        }

        string? output = response.GetOutputText();
        if (!string.IsNullOrEmpty(output))
        {
            if (toolCalls == 0)
                return output;
            return output + $"({toolCalls} {(toolCalls == 1 ? "Toolaufruf" : "Toolaufrufe")})";
        }

        output = response.Error?.Message;
        if (!string.IsNullOrEmpty(output))
        {
            Logger.LogError("OpenAI call finished with an error: {Error}. Depth: {Depth}. Total Token Count: {TokenCount}.", output, depth, response.Usage.TotalTokenCount);
            return $"Fehler bei der OpenAI-Antwort: {output}";
        }
        
        Logger.LogError($"OpenAI call returned no output or error. Depth: {depth}. Total Token Count: {response.Usage.TotalTokenCount}");
        return "Keine Antwort von OpenAI erhalten.";
    }

    private async Task HandleFunctionCall(FunctionCallResponseItem functionCall, List<ResponseItem> instructions)
    {
        switch (functionCall.FunctionName)
        {
            case WEATHER_TOOL_NAME:
                {
                    using JsonDocument argumentsJson = JsonDocument.Parse(functionCall.FunctionArguments);
                    bool hasLocation = argumentsJson.RootElement.TryGetProperty("location", out JsonElement location);
                    if (!hasLocation)
                        throw new ArgumentNullException(nameof(location), "The location argument is required for the weather tool.");

                    try
                    {
                        string weatherResponse = await ToolService.GetCurrentWeatherAsync(location.GetString()!).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(weatherResponse))
                        {
                            Logger.LogWarning("Weather tool call returned no response.");
                            instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, "Keine Wetterdaten gefunden."));
                        }
                        instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, weatherResponse));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "An error occurred while calling the weather tool.");
                        instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, $"Fehler beim Abrufen der Wetterdaten. {ex.Message}"));
                    }
                    break;
                }
            case FORECAST_TOOL_NAME:
                {
                    using JsonDocument argumentsJson = JsonDocument.Parse(functionCall.FunctionArguments);
                    bool hasLocation = argumentsJson.RootElement.TryGetProperty("location", out JsonElement location);
                    if (!hasLocation)
                        throw new ArgumentNullException(nameof(location), "The location argument is required for the weather forecast tool.");

                    try
                    {
                        string forecastResponse = await ToolService.GetWeatherForecastAsync(location.GetString()!).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(forecastResponse))
                        {
                            Logger.LogWarning("Weather forecast tool call returned no response.");
                            instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, "Keine Wettervorhersage gefunden."));
                        }
                        instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, forecastResponse));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "An error occurred while calling the weather forecast tool.");
                        instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, $"Fehler beim Abrufen der Wettervorhersage. {ex.Message}"));
                    }
                    break;
                }
            case HEISE_TOOL_NAME:
                try
                {
                    string heiseResponse = await ToolService.GetHeiseHeadlinesAsync(15).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(heiseResponse))
                    {
                        Logger.LogWarning("Heise tool call returned no response.");
                        instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, "Keine Heise Online Nachrichten gefunden."));
                    }
                    instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, heiseResponse));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "An error occurred while calling the Heise tool.");
                    instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, $"Fehler beim Abrufen der Heise Online Nachrichten. {ex.Message}"));
                }
                break;
            case POSTILLON_TOOL_NAME:
                try
                {
                    string postillonResponse = await ToolService.GetPostillonHeadlinesAsync(10).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(postillonResponse))
                    {
                        Logger.LogWarning("Postillon tool call returned no response.");
                        instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, "Keine Postillon Nachrichten gefunden."));
                    }
                    instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, postillonResponse));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "An error occurred while calling the Postillon tool.");
                    instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, $"Fehler beim Abrufen der Postillon Online Nachrichten. {ex.Message}"));
                }
                break;
            default:
                Logger.LogWarning("OpenAI called an unknown tool: {ToolName}.", functionCall.FunctionName);
                instructions.Add(ResponseItem.CreateFunctionCallOutputItem(functionCall.CallId, $"Unbekannter Toolaufruf: {functionCall.FunctionName}"));
                break;
        }
    }

    public static string SanitizeName(string participantName)
    {
        ArgumentNullException.ThrowIfNull(participantName, nameof(participantName));

        string withoutSpaces = participantName.Replace(" ", "_");
        string normalized = withoutSpaces.Normalize(NormalizationForm.FormD);
        string safeName = Regex.Replace(normalized, @"[^a-zA-Z0-9_-]+", "");
        if (safeName.Length > 100)
            safeName = safeName.Substring(0, 100);

        safeName = safeName.Trim('_');
        return safeName;
    }

    public static bool IsValidName(string name)
    {
        return !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$");
    }
}

public record AIMessage(bool IsSelf, string Message, string ParticipantName);
