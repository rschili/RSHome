using RSHome.Services;
using NSubstitute;
using DotNetEnv.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Logging;

namespace RSHome.Tests.Integration;

public class OpenAITests
{
    //[Test, Explicit]
    public async Task SendGenericRequest()
    {
        var env = DotNetEnv.Env.NoEnvVars().TraversePath().Load().ToDotEnvDictionary();
        string openAiKey = env["OPENAI_API_KEY"];
        if (string.IsNullOrEmpty(openAiKey))
        {
            Assert.Fail("OPENAI_API_KEY is not set in the .env file.");
            return;
        }

        var config = Substitute.For<IConfigService>();
        config.OpenAiApiKey.Returns(openAiKey);
        var toolService = Substitute.For<IToolService>();
        var service = new OpenAIService(config, NullLogger<OpenAIService>.Instance, toolService);

        List<AIMessage> messages = new()
        {
            new AIMessage(false, "Hey, wir geht's?", "sikk"),
            new AIMessage(true, "Hi [sikk], mir geht's gut, danke!", "Wernstrom"),
            new AIMessage(false, "Was ist dein Lieblingsessen?", "sikk"),
        };
        var response = await service.GenerateResponseAsync(DiscordWorkerService.DEFAULT_INSTRUCTION, messages).ConfigureAwait(false);
        await Assert.That(response).IsNotNullOrEmpty();
        var logger = TestContext.Current?.GetDefaultLogger();
        if (logger != null)
            await logger.LogInformationAsync($"Response: {response}");
    }
}