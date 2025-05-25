using RSHome.Services;
using NSubstitute;
using DotNetEnv.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Logging;
using System.Globalization;

namespace RSHome.Tests.Integration;

public class ToolTests
{
    [Test, Explicit]
    public async Task ObtainWeatherInDielheim()
    {
        var env = DotNetEnv.Env.NoEnvVars().TraversePath().Load().ToDotEnvDictionary();
        string apiKey = env["OPENWEATHERMAP_API_KEY"];
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Fail("OPENWEATHERMAP_API_KEY is not set in the .env file.");
            return;
        }
        var cultureInfo = new CultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

        var config = Substitute.For<IConfigService>();
        config.OpenWeatherMapApiKey.Returns(apiKey);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var service = new ToolService(config, NullLogger<ToolService>.Instance, httpClientFactory);

        var response = await service.GetCurrentWeatherAsync("Dielheim").ConfigureAwait(false);
        await Assert.That(response).IsNotNullOrEmpty();
        var logger = TestContext.Current?.GetDefaultLogger();
        if (logger != null)
            await logger.LogInformationAsync($"Response for Dielheim: {response}");

        var response2 = await service.GetCurrentWeatherAsync("69234").ConfigureAwait(false);
        await Assert.That(response2).IsNotNullOrEmpty();
        if (logger != null)
            await logger.LogInformationAsync($"Response for ZIP 69234: {response2}");
    }

    [Test, Explicit]
    public async Task ObtainWeatherForecastInDielheim()
    {
        var env = DotNetEnv.Env.NoEnvVars().TraversePath().Load().ToDotEnvDictionary();
        
        string apiKey = env["OPENWEATHERMAP_API_KEY"];
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Fail("OPENWEATHERMAP_API_KEY is not set in the .env file.");
            return;
        }
        var cultureInfo = new CultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

        var config = Substitute.For<IConfigService>();
        config.OpenWeatherMapApiKey.Returns(apiKey);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var service = new ToolService(config, NullLogger<ToolService>.Instance, httpClientFactory);

        var response = await service.GetWeatherForecastAsync("Dielheim").ConfigureAwait(false);
        await Assert.That(response).IsNotNullOrEmpty();
        var logger = TestContext.Current?.GetDefaultLogger();
        if (logger != null)
            await logger.LogInformationAsync($"Response for Dielheim: {response}");

        var response2 = await service.GetWeatherForecastAsync("69234").ConfigureAwait(false);
        await Assert.That(response2).IsNotNullOrEmpty();
        if (logger != null)
            await logger.LogInformationAsync($"Response for ZIP 69234: {response2}");
    }
}