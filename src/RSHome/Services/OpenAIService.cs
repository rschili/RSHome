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
        foreach (var input in inputs)
        {
            var participantName = input.ParticipantName;
            if(participantName == null || participantName.Length >= 100)
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
                var message = ChatMessage.CreateUserMessage($"({input.ParticipantName}) {input.Message}");
                if (input.ParticipantName != null)
                    message.ParticipantName = participantName;
                instructions.Add(message);
            }
        }

        try
        {
            var response = await Client.CompleteChatAsync(instructions, options).ConfigureAwait(false);
            bool isLengthFinishReason = response.Value.FinishReason == ChatFinishReason.Length;
            if (!isLengthFinishReason && response.Value.FinishReason != ChatFinishReason.Stop)
            {
                Logger.LogWarning($"OpenAI call did not finish with Stop. Value was {response.Value.FinishReason}");
                return null;
            }

            Logger.LogInformation("OpenAI call completed. Total Token Count: {TokenCount}.", response.Value.Usage.TotalTokenCount);
            foreach (var content in response.Value.Content)
            {
                if (content.Kind != ChatMessageContentPartKind.Text || !string.IsNullOrEmpty(content.Text))
                    return content.Text + (isLengthFinishReason ? "... (Tokenlimit erreicht)" : string.Empty);
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

    public static string SanitizeName(string participantName)
    {
        ArgumentNullException.ThrowIfNull(participantName, nameof(participantName));

        string withoutSpaces = participantName.Replace(" ", "_");
        string normalized = withoutSpaces.Normalize(NormalizationForm.FormD);
        string safeName = Regex.Replace(normalized, @"[^a-zA-Z0-9_-]+", "");
        if(safeName.Length > 100)
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
