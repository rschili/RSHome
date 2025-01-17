using Microsoft.Extensions.Hosting;
using RSMatrix;
using RSMatrix.Models;

namespace RSHome.Services;

public class MatrixWorkerService : BackgroundService
{
    private ILogger Logger { get; init; }
    private IConfigService Config { get; init; }
    private IHttpClientFactory HttpClientFactory { get; init; }

    public bool IsRunning => _client != null;

    private MatrixTextClient? _client;

    public MatrixWorkerService(ILogger<DiscordWorkerService> logger, IConfigService config, IHttpClientFactory httpClientFactory)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!Config.MatrixEnable)
        {
            Logger.LogInformation("Matrix is disabled.");
            return;
        }
        if (IsRunning)
        {
            Logger.LogWarning("Matrix worker service is already running.");
            return;
        }

        var client = await MatrixTextClient.ConnectAsync(Config.MatrixUserId, Config.MatrixPassword, "MatrixBot-342",
        HttpClientFactory, stoppingToken,
        Logger);

        //client.DebugMode = true;
        await client.SyncAsync(MessageReceivedAsync);
        Logger.LogInformation("Matrix Sync has ended.");
        _client = null;
    }

    private async Task MessageReceivedAsync(ReceivedTextMessage message)
    {
        try
        {
        var age = DateTimeOffset.Now - message.Timestamp; // filter the message spam we receive from the server at start
        if(age.TotalSeconds > 10)
            return;

        Logger.LogInformation("Received message from {Sender}: {Body}", message.Sender.GetDisplayName(), message.Body);

        if(message.Body?.Contains("ping") == true && age.TotalSeconds < 10)
        {
            await message.Room.SendTypingNotificationAsync().ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
            await message.SendResponseAsync("pong!").ConfigureAwait(false);
        }
        }
        catch (Exception ex)
        {
        Logger.LogError(ex, "An error occurred while processing a message.");
        }
    }
}