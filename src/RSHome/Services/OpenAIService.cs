using System.Text;
using System.Text.RegularExpressions;
using OpenAI.Chat;
using RSMatrix.Http;

namespace RSHome.Services;

public class OpenAIService
{
    public IConfigService Config { get; private init; }
    public ChatClient Client { get; private init; }
    public ILogger Logger { get; private init; }

    public LeakyBucketRateLimiter RateLimiter { get; private init; } = new(10, 60);

    public OpenAIService(IConfigService config, ILogger<OpenAIService> logger)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Client = new ChatClient(model: "gpt-4o", apiKey: config.OpenAiApiKey);
    }

    public async Task<string?> GenerateResponseAsync(string systemPrompt, IEnumerable<AIMessage> inputs)
    {
        if (!RateLimiter.Leak())
            return null;

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 100,
            ResponseFormat = ChatResponseFormat.CreateTextFormat(),
        };

        var instructions = new List<ChatMessage>() { ChatMessage.CreateSystemMessage(systemPrompt) };
        Dictionary<string, string> replacedParticipantNames = new();
        foreach (var input in inputs)
        {
            var participantName = input.participantName;
            if(participantName == null || participantName.Length >= 100)
                continue; // skip this message

            if (!IsValidName(participantName))
            {
                participantName = SanitizeName(participantName, replacedParticipantNames);
            }

            if (input.isSelf)
            {
                var message = ChatMessage.CreateAssistantMessage(input.message);
                if (input.participantName != null)
                    message.ParticipantName = participantName;
                instructions.Add(message);
            }
            else
            {
                var message = ChatMessage.CreateUserMessage(input.message);
                if (input.participantName != null)
                    message.ParticipantName = participantName;
                instructions.Add(message);
            }
        }

        try
        {
            var response = await Client.CompleteChatAsync(instructions, options).ConfigureAwait(false);
            if (response.Value.FinishReason != ChatFinishReason.Stop)
            {
                Logger.LogWarning($"OpenAI call did not finish with Stop. Value was {response.Value.FinishReason}");
                return null;
            }
            Logger.LogInformation("OpenAI call completed. Total Token Count: {TokenCount}.", response.Value.Usage.TotalTokenCount);
            foreach (var content in response.Value.Content)
            {
                if (content.Kind != ChatMessageContentPartKind.Text || !string.IsNullOrEmpty(content.Text))
                    return RestoreNames(content.Text, replacedParticipantNames);
            }

            Logger.LogWarning("OpenAI call did not return any text content.");
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred during the OpenAI call.");
            return null;
        }
    }

    internal static string SanitizeName(string participantName, Dictionary<string, string> replacedParticipantNames)
    {
        ArgumentNullException.ThrowIfNull(participantName, nameof(participantName));
        ArgumentNullException.ThrowIfNull(replacedParticipantNames, nameof(replacedParticipantNames));

        if (replacedParticipantNames.TryGetValue(participantName, out var sanitizedName))
            return sanitizedName;

        string normalized = participantName.Normalize(NormalizationForm.FormD);
        string safeName = Regex.Replace(normalized, @"[^a-zA-Z0-9_-]+", "");

        replacedParticipantNames[participantName] = safeName;
        return safeName;
    }

    internal static string RestoreNames(string response, Dictionary<string, string> replacedParticipantNames)
    {
        ArgumentNullException.ThrowIfNull(response, nameof(response));
        ArgumentNullException.ThrowIfNull(replacedParticipantNames, nameof(replacedParticipantNames));

        if (replacedParticipantNames.Count == 0)
            return response;

        StringBuilder sb = new(response);
        foreach (var (originalName, sanitizedName) in replacedParticipantNames)
        {
            sb.Replace(sanitizedName, originalName);
        }

        return sb.ToString();
    }

    private bool IsValidName(string name)
    {
        return !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$");
    }
}

public record AIMessage(bool isSelf, string message, string participantName);
