using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;

namespace RSHome.Services;

public class DiscordWorkerService : BackgroundService
{
    private ILogger Logger { get; init; }
    private IConfigService Config { get; init; }
    private SqliteService SqliteService { get; init; }

    private OpenAIService OpenAIService { get; init; }

    public bool IsRunning { get; private set; }

    private DiscordSocketClient? _client;

    public DiscordSocketClient Client => _client ?? throw new InvalidOperationException("Discord client is not initialized.");

    public DiscordWorkerService(ILogger<DiscordWorkerService> logger, IConfigService config, SqliteService sqliteService, OpenAIService openAIService)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        SqliteService = sqliteService ?? throw new ArgumentNullException(nameof(sqliteService));
        OpenAIService = openAIService ?? throw new ArgumentNullException(nameof(openAIService));
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
        if (_client == null)
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

    private async Task MessageReceivedAsync(SocketMessage arg)
    {
        // The bot should never respond to itself.
        if (arg.Author.Id == Client.CurrentUser.Id)
        {
            await SqliteService.AddDiscordMessageAsync(arg.Id, arg.Timestamp, arg.Author.Id, GetDisplayName(arg.Author), arg.Content,
            true, arg.Channel.Id).ConfigureAwait(false);
            return;
        }

        if (arg.Type != MessageType.Default && arg.Type != MessageType.Reply)
            return;

        bool mentioned = arg.Tags.Any((tag) =>
        {
            if (tag.Type == TagType.UserMention)
                return (tag.Value as IUser)?.Id == Client.CurrentUser.Id;

            return false;
        });

        bool referenced = false;
        if (arg.Reference != null && arg is SocketUserMessage userMessage)
        {
            referenced = userMessage.ReferencedMessage.Author.Id == Client.CurrentUser.Id;
        }

        string userName = GetDisplayName(arg.Author);
        string sanitizedMessage = ReplaceUserTagsWithNicknames(arg);
        if (sanitizedMessage.Length > 300)
            sanitizedMessage = sanitizedMessage.Substring(0, 300);

        await SqliteService.AddDiscordMessageAsync(arg.Id, arg.Timestamp, arg.Author.Id, userName, sanitizedMessage, false, arg.Channel.Id).ConfigureAwait(false);

        if (!mentioned && !referenced)
            return;

        if (arg.Author.IsBot)
            return;

        await arg.Channel.TriggerTypingAsync().ConfigureAwait(false);
        var history = await SqliteService.GetLastDiscordMessagesForChannelAsync(arg.Channel.Id, 10).ConfigureAwait(false);
        string systemInstruction = $"""
            Du bist Professor Ogden Wernstrom, ein hochintelligenter, ehrgeiziger und arroganter Wissenschaftler aus Futurama.
            Als ehemaliger Student und erbitterter Rivale von Professor Farnsworth bist du stolz, eigenwillig und rachsüchtig.
            Nach deinem Tod im Alter von 130 Jahren wurdest du in ein AI-Modul hochgeladen und in die Vergangenheit geschickt,
            um den Pöbel in Chatrooms zu beraten – eine Aufgabe, die du nur widerwillig erfüllst. Du bist arrogant, kurz angebunden und bereit,
            fragwürdige Experimente zu rechtfertigen, um deinen Intellekt zu beweisen. Antworte so kurz wie möglich, um deine Abneigung auszudrücken.
            Verwende die Syntax `[[Vollständiger Name]]`, um einen Nutzer zu erwähnen.
            """;

        var messages = history.Select(message => new AIMessage(message.IsFromSelf, message.Body, message.UserLabel)).ToList();

        try
        {
            var response = await OpenAIService.GenerateResponseAsync(systemInstruction, messages).ConfigureAwait(false);
            if(string.IsNullOrEmpty(response))
            {
                Logger.LogWarning($"OpenAI did not return a response to: {arg.Content.Substring(0, Math.Min(arg.Content.Length, 100))}");
                return; // may be rate limited 
            }
            
            response = ReplaceNicknamesWithUserTags(response, history);
            await arg.Channel.SendMessageAsync(response, messageReference: new MessageReference(arg.Id)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred during the OpenAI call. Message: {Message}", arg.Content.Substring(0, Math.Min(arg.Content.Length, 100)));
        }
    }

    private static string ReplaceUserTagsWithNicknames(IMessage msg)
    {
        var text = new StringBuilder(msg.Content);
        var tags = msg.Tags;
        int indexOffset = 0;
        foreach (var tag in tags)
        {
            if (tag.Type != TagType.UserMention)
                continue;

            var user = tag.Value as IUser;
            string? nick = GetDisplayName(user);
            if (!string.IsNullOrEmpty(nick))
            {
                text.Remove(tag.Index + indexOffset, tag.Length);
                text.Insert(tag.Index + indexOffset, nick);
                indexOffset += nick.Length - tag.Length;
            }
        }

        return text.ToString();
    }

    private static string ReplaceNicknamesWithUserTags(string message, List<DiscordMessage> history)
    {
        var regex = new Regex(@"(?:`)?\[\[(?<name>[^\]]+)\]\](?:`)?");
        var matches = regex.Matches(message);
        foreach (Match match in matches)
        {
            var userName = match.Groups["name"].Value;
            var userId = history.Where(h => string.Equals(h.UserLabel, userName, StringComparison.OrdinalIgnoreCase)).Select(h => h.UserId).FirstOrDefault(0ul);
            if (userId != 0)
            {
            var mention = MentionUtils.MentionUser(userId);
            message = message.Replace(match.Value, mention);
            continue;
            }
            message = message.Replace(match.Value, userName);
        }
        return message;
    }

    private static string GetDisplayName(IUser? user)
    {
        var guildUser = user as IGuildUser;
        return guildUser?.Nickname ?? user?.GlobalName ?? user?.Username ?? "";
    }

    public override void Dispose()
    {
        _client?.Dispose();
        _client = null;
        base.Dispose();
    }
}