using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace RSHome.Services;

public class DiscordWorkerService : BackgroundService
{
    public ILogger Logger { get; init; }
    private IConfigService Config { get; init; }
    private SqliteService SqliteService { get; init; }

    private OpenAIService OpenAIService { get; init; }

    public bool IsRunning { get; private set; }

    private DiscordSocketClient? _client;

    public DiscordSocketClient Client => _client ?? throw new InvalidOperationException("Discord client is not initialized.");

    private bool IsInDialogueMode => RemainingDialogueMessages > 0;

    private int RemainingDialogueMessages = 0;

    public List<JoinedTextChannel> TextChannels { get; private set; } = new();

    private const string DEFAULT_INSTRUCTION = $"""
        Du bist Professor Ogden Wernstrom, ein hochintelligenter, ehrgeiziger und arroganter Wissenschaftler aus Futurama.
        Als ehemaliger Student und erbitterter Rivale von Professor Farnsworth bist du stolz, eigenwillig und rachsüchtig.
        Nach deinem Tod im Alter von 130 Jahren wurdest du in ein AI-Modul hochgeladen und in die Vergangenheit geschickt,
        um den Pöbel in Chatrooms zu beraten - eine Aufgabe, die du nur widerwillig erfüllst. Du bist arrogant, kurz angebunden und bereit,
        fragwürdige Experimente zu rechtfertigen, um deinen Intellekt zu beweisen. Antworte so kurz wie möglich, um deine Abneigung auszudrücken.
        Verwende die Syntax `[[Vollständiger Name]]`, um einen Nutzer zu erwähnen.
        """;

    public DiscordWorkerService(ILogger<DiscordWorkerService> logger, IConfigService config, SqliteService sqliteService, OpenAIService openAIService)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        SqliteService = sqliteService ?? throw new ArgumentNullException(nameof(sqliteService));
        OpenAIService = openAIService ?? throw new ArgumentNullException(nameof(openAIService));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.GuildMembers;
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

    private async Task ReadyAsync()
    {
        if (_client == null)
            return;

        _client.MessageReceived += MessageReceived;

        Logger.LogInformation($"Discord User {_client.CurrentUser} is connected!");
        TextChannels = await GetTextChannels();
        IsRunning = true;
    }

    private Task MessageReceived(SocketMessage arg)
    {
        // There is a timeout on the MessageReceived event, so we need to use a Task.Run to avoid blocking the event loop
        _ = Task.Run(() => MessageReceivedAsync(arg)).ContinueWith(task =>
        {
            if (task.Exception != null)
            {
                Logger.LogError(task.Exception, "An error occurred while processing a message.");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
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
        Logger.Log(logLevel, message.Exception, message.Message);
        return Task.CompletedTask;
    }

    private async Task MessageReceivedAsync(SocketMessage arg)
    {
        if (arg.Type != MessageType.Default && arg.Type != MessageType.Reply)
            return;

        string userName = GetDisplayName(arg.Author);
        string sanitizedMessage = ReplaceUserTagsWithNicknames(arg);
        if (sanitizedMessage.Length > 300)
            sanitizedMessage = sanitizedMessage.Substring(0, 300);

        // The bot should never respond to itself.
        if (arg.Author.Id == Client.CurrentUser.Id)
        {
            await SqliteService.AddDiscordMessageAsync(arg.Id, arg.Timestamp, arg.Author.Id, GetDisplayName(arg.Author), sanitizedMessage, true, arg.Channel.Id).ConfigureAwait(false);
            return;
        }

        await SqliteService.AddDiscordMessageAsync(arg.Id, arg.Timestamp, arg.Author.Id, userName, sanitizedMessage, false, arg.Channel.Id).ConfigureAwait(false);

        if (IsInDialogueMode)
            Interlocked.Decrement(ref RemainingDialogueMessages);
        else if (!ShouldRespond(arg))
            return;

        await arg.Channel.TriggerTypingAsync().ConfigureAwait(false);
        var history = await SqliteService.GetLastDiscordMessagesForChannelAsync(arg.Channel.Id, 10).ConfigureAwait(false);
        var messages = history.Select(message => new AIMessage(message.IsFromSelf, message.Body, message.UserLabel)).ToList();

        try
        {
            var response = await OpenAIService.GenerateResponseAsync(DEFAULT_INSTRUCTION, messages).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response))
            {
                Logger.LogWarning($"OpenAI did not return a response to: {arg.Content.Substring(0, Math.Min(arg.Content.Length, 100))}");
                return; // may be rate limited 
            }

            var userNameUserIdMap = GenerateUserNameUserIdMap(history);
            response = ReplaceNicknamesWithUserTags(response, userNameUserIdMap, out var hasMentions);
            await arg.Channel.SendMessageAsync(response, messageReference: hasMentions ? null : new MessageReference(arg.Id)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred during the OpenAI call. Message: {Message}", arg.Content.Substring(0, Math.Min(arg.Content.Length, 100)));
        }
    }

    private bool ShouldRespond(SocketMessage arg)
    {
        if (arg.Author.IsBot)
            return false;

        //mentions
        if (arg.Tags.Any((tag) => tag.Type == TagType.UserMention && (tag.Value as IUser)?.Id == Client.CurrentUser.Id))
            return true;

        if (arg.Reference != null && arg is SocketUserMessage userMessage &&
            userMessage.ReferencedMessage.Author.Id == Client.CurrentUser.Id)
            return true;

        return false;
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

    private static string ReplaceNicknamesWithUserTags(string message, Dictionary<string, ulong> userNameUserIdMap, out bool hasMentions)
    {
        hasMentions = false;
        var regex = new Regex(@"(?:`)?\[\[(?<name>[^\]]+)\]\](?:`)?");
        var matches = regex.Matches(message);
        foreach (Match match in matches)
        {
            var userName = match.Groups["name"].Value;
            if (userNameUserIdMap.TryGetValue(userName, out var userId))
            {
                var mention = MentionUtils.MentionUser(userId);
                message = message.Replace(match.Value, mention);
            }
            else
            {
                message = message.Replace(match.Value, userName);
            }
            hasMentions = true;
        }
        return message;
    }

    private static Dictionary<string, ulong> GenerateUserNameUserIdMap(List<DiscordMessage> history)
    {
        return history
            .GroupBy(h => h.UserLabel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().UserId, StringComparer.OrdinalIgnoreCase);
    }

    internal async Task StartDialogueAsync(string name, ulong userId, ulong channelId, int messagesCount)
    {
        if (IsInDialogueMode)
        {
            throw new InvalidOperationException("Dialogue mode is already active.");
        }
        RemainingDialogueMessages = messagesCount - 1;
        var channel = await Client.GetChannelAsync(channelId).ConfigureAwait(false);
        if (channel == null)
        {
            throw new InvalidOperationException($"Channel not found: {channelId}");
        }
        if (channel.ChannelType != ChannelType.Text || !(channel is ITextChannel textChannel))
        {
            throw new InvalidOperationException($"Channel is not a text channel: {channelId}");
        }

        await textChannel.TriggerTypingAsync().ConfigureAwait(false);
        var history = await SqliteService.GetLastDiscordMessagesForChannelAsync(textChannel.Id, 10).ConfigureAwait(false);
        var messages = history.Select(message => new AIMessage(message.IsFromSelf, message.Body, message.UserLabel)).ToList();
        var extendedInstruction = $@"{DEFAULT_INSTRUCTION}{Environment.NewLine}Denk dir ein Thema aus und beginne ein Gespräch mit [[{name}]].";

        try
        {
            var response = await OpenAIService.GenerateResponseAsync(extendedInstruction, messages).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response))
            {
                Logger.LogWarning("OpenAI did not return a response to starting dialogue.");
                return; // may be rate limited 
            }

            var userNameUserIdMap = GenerateUserNameUserIdMap(history);
            userNameUserIdMap[name] = userId;
            response = ReplaceNicknamesWithUserTags(response, userNameUserIdMap, out _);
            await textChannel.SendMessageAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred during the OpenAI call to start a dialogue.");
        }
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

    private async Task<List<JoinedTextChannel>> GetTextChannels()
    {
        List<JoinedTextChannel> channels = new();
        foreach(var server in Client.Guilds)
        {
            await server.DownloadUsersAsync();
            foreach(var channel in server.Channels)
            {
                if(channel.ChannelType == ChannelType.Text)
                {
                    var cache = new JoinedTextChannel($"{server.Name} -> {channel.Name}", channel.Id, 
                        GetChannelUsers(channel));

                    channels.Add(cache);
                }
            }
        }
        return channels;
    }

    private List<ChannelUser> GetChannelUsers(SocketGuildChannel channel)
    {
        List<ChannelUser> users = new();
        
        foreach(var user in channel.Users)
        {
            var displayName = GetDisplayName(user);
            var openAIName = OpenAIService.IsValidName(displayName) ? displayName : OpenAIService.SanitizeName(displayName);
            users.Add(new ChannelUser(displayName, user.Id, openAIName));
        }
        return users;
    }
}

public record JoinedTextChannel(string Name, ulong Id, List<ChannelUser> Users);

public record ChannelUser(string Name, ulong Id, string OpenAIName);