using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;
using RSMatrix.Http;

namespace RSHome.Services;

public class OpenAIService
{
    public IConfigService Config { get; private init; }
    public ChatClient Client { get; private init; }
    public ILogger Logger { get; private init; }

    public IToolService ToolService { get; private init; }

    public LeakyBucketRateLimiter RateLimiter { get; private init; } = new(10, 60);

    public OpenAIService(IConfigService config, ILogger<OpenAIService> logger, IToolService toolService)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Client = new ChatClient(model: "gpt-4.1", apiKey: config.OpenAiApiKey); //  /*"o1" "o3-mini" "gpt-4o"*/
        ToolService = toolService ?? throw new ArgumentNullException(nameof(toolService));
    }
    
    private const string WEATHER_TOOL_NAME = "weather_current";
    private static readonly ChatTool weatherCurrentTool = ChatTool.CreateFunctionTool
    (
        functionName: WEATHER_TOOL_NAME,
        functionDescription: "Get the current weather and sunrise/sunset for a given location",
        functionParameters: BinaryData.FromBytes("""
            {
                "type": "object",
                "properties": {
                    "location": {
                        "type": "string",
                        "description": "City name or ZIP code to get the current weather for."
                    }
                },
                "required": [ "location" ]
            }
            """u8.ToArray())
    );

    private const string FORECAST_TOOL_NAME = "weather_forecast";
    private static readonly ChatTool weatherForecastTool = ChatTool.CreateFunctionTool
    (
        functionName: FORECAST_TOOL_NAME,
        functionDescription: "Get a 5 day weather forecast for a given location",
        functionParameters: BinaryData.FromBytes("""
            {
                "type": "object",
                "properties": {
                    "location": {
                        "type": "string",
                        "description": "City name or ZIP code to get the forecast for."
                    }
                },
                "required": [ "location" ]
            }
            """u8.ToArray())
    );

    private static readonly ChatCompletionOptions options = new()
    {
        MaxOutputTokenCount = 1000,
        ResponseFormat = ChatResponseFormat.CreateTextFormat(),
        Tools = { weatherCurrentTool, weatherForecastTool },
    };

    public async Task<string?> GenerateResponseAsync(string systemPrompt, IEnumerable<AIMessage> inputs)
    {
        if (!RateLimiter.Leak())
            return null;

        systemPrompt = $"""
            {systemPrompt}
            Aktuell ist [{DateTime.Now.ToLongDateString()}, {DateTime.Now.ToLongTimeString()} Uhr. 
            """;

        var instructions = new List<ChatMessage>() { ChatMessage.CreateDeveloperMessage(systemPrompt) };
        foreach (var input in inputs)
        {
            var participantName = input.ParticipantName;
            if (participantName == null || participantName.Length >= 100)
                throw new ArgumentException("Participant name is too long.", nameof(participantName));

            if (!IsValidName(participantName))
                throw new ArgumentException("Participant name is invalid.", nameof(participantName));

            if (input.IsSelf)
            {
                var message = ChatMessage.CreateAssistantMessage(input.Message);
                if (input.ParticipantName != null)
                    message.ParticipantName = participantName;
                instructions.Add(message);
            }
            else
            {
                var message = ChatMessage.CreateUserMessage($"[[{input.ParticipantName}]] {input.Message}");
                if (input.ParticipantName != null)
                    message.ParticipantName = participantName;
                instructions.Add(message);
            }
        }

        try
        {
            return await CompleteChatAsync(instructions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred during the OpenAI call.");
            return $"Fehler bei der Kommunikation mit OpenAI: {ex.Message}";
        }
    }

    public async Task<string> CompleteChatAsync(List<ChatMessage> instructions, int depth = 1)
    {
        ArgumentNullException.ThrowIfNull(instructions, nameof(instructions));

        var response = await Client.CompleteChatAsync(instructions, options).ConfigureAwait(false);
        string? text;
        switch (response.Value.FinishReason)
        {
            case ChatFinishReason.Length:
                Logger.LogWarning("Call reached token limit. Depth: {Depth}. Total Token Count: {TokenCount}.", depth, response.Value.Usage.TotalTokenCount);
                text = GetCompletionText(response.Value);
                if (!string.IsNullOrEmpty(text))
                    return text + "... (Tokenlimit erreicht)";

                return string.Empty;
            case ChatFinishReason.Stop:
                Logger.LogInformation("OpenAI call completed successfully. Depth: {Depth}. Total Token Count: {TokenCount}.", depth, response.Value.Usage.TotalTokenCount);
                text = GetCompletionText(response.Value);
                if (!string.IsNullOrEmpty(text))
                    return text;

                return string.Empty;

            case ChatFinishReason.ToolCalls:
                Logger.LogInformation("OpenAI call requested a tool call. Depth: {Depth}", depth);
                if (depth >= 3)
                {
                    Logger.LogWarning("Maximum depth reached.");
                    return "(Maximale Anzahl an Toolaufrufen erreicht)";
                }
                instructions.Add(new AssistantChatMessage(response));
                foreach (ChatToolCall toolCall in response.Value.ToolCalls)
                {
                    switch (toolCall.FunctionName)
                    {
                        case WEATHER_TOOL_NAME:
                            {
                                using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
                                bool hasLocation = argumentsJson.RootElement.TryGetProperty("location", out JsonElement location);
                                if (!hasLocation)
                                    throw new ArgumentNullException(nameof(location), "The location argument is required for the weather tool.");

                                try
                                {
                                    string weatherResponse = await ToolService.GetCurrentWeatherAsync(location.GetString()!).ConfigureAwait(false);
                                    if (string.IsNullOrEmpty(weatherResponse))
                                    {
                                        Logger.LogWarning("Weather tool call returned no response.");
                                        instructions.Add(new ToolChatMessage(toolCall.Id, "Keine Wetterdaten gefunden."));
                                    }
                                    instructions.Add(new ToolChatMessage(toolCall.Id, weatherResponse));
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "An error occurred while calling the weather tool.");
                                    instructions.Add(new ToolChatMessage(toolCall.Id, $"Fehler beim Abrufen der Wetterdaten. {ex.Message}"));
                                }
                                break;
                            }
                        case FORECAST_TOOL_NAME:
                            {
                                using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
                                bool hasLocation = argumentsJson.RootElement.TryGetProperty("location", out JsonElement location);
                                if (!hasLocation)
                                    throw new ArgumentNullException(nameof(location), "The location argument is required for the weather forecast tool.");

                                try
                                {
                                    string forecastResponse = await ToolService.GetWeatherForecastAsync(location.GetString()!).ConfigureAwait(false);
                                    if (string.IsNullOrEmpty(forecastResponse))
                                    {
                                        Logger.LogWarning("Weather forecast tool call returned no response.");
                                        instructions.Add(new ToolChatMessage(toolCall.Id, "Keine Wettervorhersage gefunden."));
                                    }
                                    instructions.Add(new ToolChatMessage(toolCall.Id, forecastResponse));
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "An error occurred while calling the weather forecast tool.");
                                    instructions.Add(new ToolChatMessage(toolCall.Id, $"Fehler beim Abrufen der Wettervorhersage. {ex.Message}"));
                                }
                                break;
                            }
                        default:
                            Logger.LogWarning("OpenAI called an unknown tool: {ToolName}. Depth: {Depth}", toolCall.FunctionName, depth);
                            instructions.Add(new ToolChatMessage(toolCall.Id, $"Unbekannter Toolaufruf: {toolCall.FunctionName}"));
                            break;
                    }
                }

                return await CompleteChatAsync(instructions, depth + 1).ConfigureAwait(false);

            case ChatFinishReason.ContentFilter:
                Logger.LogWarning("OpenAI call was filtered by content filter. Depth: {Depth}. Total Token Count: {TokenCount}.", depth, response.Value.Usage.TotalTokenCount);
                return "(Inhalt von OpenAI gefiltert)";

            default:
                Logger.LogError("OpenAI call finished with an unknown reason: {FinishReason}. Depth: {Depth}. Total Token Count: {TokenCount}.", response.Value.FinishReason, depth, response.Value.Usage.TotalTokenCount);
                return "(Unbekannter Abschlussgrund von OpenAI)";
        }
    }

    private string GetCompletionText(ChatCompletion completion)
    {
        StringBuilder sb = new();
        foreach (var content in completion.Content)
        {
            if (content.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(content.Text))
            {
                sb.Append(content.Text);
            }
            if(content.Kind == ChatMessageContentPartKind.Refusal && !string.IsNullOrEmpty(content.Text))
            {
                Logger.LogWarning("OpenAI refused to answer: {Text}", content.Text);
            }
        }
        return sb.ToString();
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
