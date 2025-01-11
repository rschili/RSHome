using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;

namespace RSHome.Services;

public class DiscordWorkerService : BackgroundService
{
    private ILogger Logger { get; init; }
    private IConfigService Config { get; init; }

    public bool IsRunning { get; private set; }

    private DiscordSocketClient? _client;

    public DiscordWorkerService(ILogger<DiscordWorkerService> logger, IConfigService config)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent;
        intents &= ~GatewayIntents.GuildInvites;
        intents &= ~GatewayIntents.GuildScheduledEvents;

        var discordConfig = new DiscordSocketConfig
        {
            MessageCacheSize = 100,
            GatewayIntents = intents
        };
        _client = new DiscordSocketClient(discordConfig);
        /*var aiClient = new ChatClient(model: "gpt-4o", apiKey: config.OpenAiApiKey);
        var archive = await Archive.CreateAsync();*/

        //var bot = new Bot { Client = client, Config = config, AI = aiClient, Archive = archive, Logger = log, Cancellation = cancellationTokenSource };
        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        await _client.LoginAsync(TokenType.Bot, Config.DiscordToken);
        await _client.StartAsync();
        await Task.Delay(Timeout.Infinite, stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            Logger.LogInformation("Cancellation requested, shutting down...");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred. Shutting down...");
        }
        IsRunning = false;
        Logger.LogInformation("Logging out...");
        await _client.LogoutAsync();
        Logger.LogInformation("Disposing client...");
        await _client.DisposeAsync();
        _client = null;
    }

    private Task ReadyAsync()
    {
        if(_client == null)
            return Task.CompletedTask;

        // There is a timeout on the MessageReceived event, so we need to use a Task.Run to avoid blocking the event loop
        _client.MessageReceived += (arg) => Task.Run(() => MessageReceivedAsync(arg)).ContinueWith(task =>
        {
            if (task.Exception != null)
            {
            Logger.LogError(task.Exception, "An error occurred while processing a message.");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
        
        Logger.LogInformation($"Discord User {_client.CurrentUser} is connected!");
        IsRunning = true;
        return Task.CompletedTask;
    }

    private Task LogAsync(LogMessage message)
    {
        var logLevel = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.None
        };
        Logger.Log(logLevel, message.Message);
        return Task.CompletedTask;
    }

    private async Task MessageReceivedAsync(SocketMessage message)
    {
        await Task.Delay(100);
    }

    public override void Dispose()
    {
        _client?.Dispose();
        _client = null;
        base.Dispose();
    }
}