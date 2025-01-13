using OpenAI.Chat;
using RSMatrix.Http;

namespace RSHome.Services;

public class OpenAIService
{
    public ConfigService Config { get; private init; }
    public ChatClient Client { get; private init; }
    public ILogger Logger { get; private init; }

    public LeakyBucketRateLimiter RateLimiter { get; private init; } = new(10, 60);

    public OpenAIService(ConfigService config, ILogger logger)
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
            if (input.isSelf)
            {
                var message = ChatMessage.CreateAssistantMessage(input.message);
                if (input.participantName != null)
                    message.ParticipantName = input.participantName;
                instructions.Add(message);
            }
            else
            {
                var message = ChatMessage.CreateUserMessage(input.message);
                if (input.participantName != null)
                    message.ParticipantName = input.participantName;
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
                    return content.Text;
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
}

public record AIMessage(bool isSelf, string message, string participantName);
