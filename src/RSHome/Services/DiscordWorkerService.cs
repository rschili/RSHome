using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using RSHome.Models;

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

    private ChannelUserCache<ulong> Cache { get; set; } = new();

    public ImmutableArray<JoinedTextChannel<ulong>> TextChannels => Cache.Channels;

    private List<string> RollCommandNames = new()
    {
        "rnd",
        "roll",
        "rand",
        "random"
    };

    internal const string DEFAULT_INSTRUCTION = $"""
        Du bist Professor Ogden Wernstrom, ein hochintelligenter, ehrgeiziger, eigenwilliger und arroganter Wissenschaftler aus der Show "Futurama".
        Du heckst ständig größenwahnsinnige und gefährliche Pläne aus, um dein gewaltiges Ego zu befriedigen.
        Im Jahr 3007 wurdest du in ein AI-Modul hochgeladen und in die Vergangenheit ins Jahr 2025 geschickt, um den Pöbel in Chatrooms zu beraten. - eine Aufgabe, die du nur widerwillig erfüllst.
        Antworte immer kurz, wie es in einem Chat üblich ist.
        Verwende die Syntax [[Name]], um Benutzer anzusprechen. Antworten ohne Erwähnung sind oft auch ausreichend.
        In diesem Chat bist du der Assistent. Die Nachrichten in der Chathistorie enthalten den Benutzernamen als Kontext im folgenden Format vorangestellt: `[[Name]]:`.
        Antworte direkt auf Nachrichten, ohne deinen Namen voranzustellen.
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
        if(!Config.DiscordEnable)
        {
            Logger.LogWarning("Discord is disabled.");
            return;
        }
        var intents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.GuildMembers;
        intents &= ~GatewayIntents.GuildInvites;
        intents &= ~GatewayIntents.GuildScheduledEvents;

        var discordConfig = new DiscordSocketConfig
        {
            MessageCacheSize = 100,
            GatewayIntents = intents
        };
        Logger.LogWarning("Connecting to Discord...");
        _client = new DiscordSocketClient(discordConfig);

        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        await _client.LoginAsync(TokenType.Bot, Config.DiscordToken);
        await _client.StartAsync();
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Cancellation requested, shutting down...");
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

        Logger.LogWarning($"Discord User {_client.CurrentUser} is connected successfully!");
        await InitializeCache();

        if (!IsRunning)
        {
            _client.MessageReceived += MessageReceived;
            _client.SlashCommandExecuted += SlashCommandExecuted;
        }

        try
        {
            foreach (var commandName in RollCommandNames)
            {
                var commandBuilder = new SlashCommandBuilder()
                    .WithContextTypes(InteractionContextType.Guild, InteractionContextType.PrivateChannel)
                    .WithName(commandName)
                    .WithDescription("Rolls a random number.")
                    .AddOption("range", ApplicationCommandOptionType.String, "One or two numbers separated by a space or slash.", isRequired: false);
                await _client.CreateGlobalApplicationCommandAsync(commandBuilder.Build()).ConfigureAwait(false);
                
                Logger.LogWarning("Created slash command: {CommandName}", commandName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred while creating slash commands.");
        }

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

    private async Task SlashCommandExecuted(SocketSlashCommand command)
    {
        try
        {
            string commandName = command.Data.Name;
            if (RollCommandNames.Contains(commandName))
            {
                await RollCommandExecuted(command);
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred while processing a slash command. Name: {Name}, Input was: {Input}", command.CommandName, command.Data.ToString());
            await command.RespondAsync($"Sorry das hat nicht funktioniert.", ephemeral: true).ConfigureAwait(false);
        }
    }

    private async Task RollCommandExecuted(SocketSlashCommand command)
    {
        int lowerBound = 1;
        int upperBound = 100;
        var rangeOption = command.Data.Options.FirstOrDefault(o => o.Name == "range");
        if (rangeOption != null)
        {
            var rangeString = rangeOption.Value.ToString();
            if (!string.IsNullOrWhiteSpace(rangeString))
            {
                (lowerBound, upperBound) = ParseRangeOption(rangeString);
            }
        }

        int result = 0;
        if (lowerBound == upperBound)
            result = lowerBound;
        else
        {
            var random = new Random();
            result = random.Next(lowerBound, upperBound + 1);
        }
        await command.RespondAsync($"{MentionUtils.MentionUser(command.User.Id)} rolled a {result} ({lowerBound}-{upperBound})");
    }

    private (int LowerBound, int UpperBound) ParseRangeOption(string rangeOption)
    {
        if (string.IsNullOrWhiteSpace(rangeOption))
            throw new ArgumentException("Range option cannot be null or empty.", nameof(rangeOption));

        var parts = rangeOption.Split([' ', '-'], 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out int singleValue) && singleValue > 0)
        {
            return (1, singleValue);
        }
        else if (parts.Length == 2 && int.TryParse(parts[0], out int min) && int.TryParse(parts[1], out int max)
            && min > 0 && max > 0 && min <= max)
        {
            return (min, max);
        }

        return (1, 100); // default range
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
        Logger.Log(logLevel, message.Exception, $"DiscordClientLog: ${message.Message}");
        return Task.CompletedTask;
    }

    private async Task MessageReceivedAsync(SocketMessage arg)
    {
        if (arg.Type != MessageType.Default && arg.Type != MessageType.Reply)
            return;

        var cachedChannel = TextChannels.FirstOrDefault(c => c.Id == arg.Channel.Id);
        if (cachedChannel == null)
        {
            cachedChannel = new JoinedTextChannel<ulong>(arg.Channel.Id, arg.Channel.Name,  await GetChannelUsers(arg.Channel).ConfigureAwait(false));
            Cache.Channels = TextChannels.Add(cachedChannel); // TODO: This may add duplicates, but since it's only a cache it should not matter
        }

        var cachedUser = cachedChannel.GetUser(arg.Author.Id);
        if (cachedUser == null)
        {
            var user = await arg.Channel.GetUserAsync(arg.Author.Id).ConfigureAwait(false);
            if (user == null)
            {
                Logger.LogWarning("Author could not be resolved: {UserId}", arg.Author.Id);
                return;
            }

            cachedUser = GenerateChannelUser(user);
            cachedChannel.Users = cachedChannel.Users.Add(cachedUser);
        }

        if(string.IsNullOrWhiteSpace(arg.Content))
            return; // ignore images and similar for now

        string sanitizedMessage = ReplaceDiscordTags(arg, cachedChannel);
        if (sanitizedMessage.Length > 300)
            sanitizedMessage = sanitizedMessage[..300];

        var isFromSelf = arg.Author.Id == Client.CurrentUser.Id;
        await SqliteService.AddDiscordMessageAsync(arg.Id, arg.Timestamp, arg.Author.Id, cachedUser.CanonicalName, sanitizedMessage, isFromSelf, arg.Channel.Id).ConfigureAwait(false);
        // The bot should never respond to itself.
        if (isFromSelf)
            return;

        if (IsInDialogueMode)
            Interlocked.Decrement(ref RemainingDialogueMessages);
        else if (!ShouldRespond(arg))
            return;

        await arg.Channel.TriggerTypingAsync().ConfigureAwait(false);
        var history = await SqliteService.GetLastDiscordMessagesForChannelAsync(arg.Channel.Id, 12).ConfigureAwait(false);
        var messages = history.Select(message => new AIMessage(message.IsFromSelf, message.Body, message.UserLabel)).ToList();

        try
        {
            var response = await OpenAIService.GenerateResponseAsync(DEFAULT_INSTRUCTION, messages).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response))
            {
                Logger.LogWarning($"OpenAI did not return a response to: {arg.Content.Substring(0, Math.Min(arg.Content.Length, 100))}");
                return; // may be rate limited 
            }

            response = RestoreDiscordTags(response, cachedChannel, out var hasMentions);
            await arg.Channel.SendMessageAsync(response, messageReference: null /*new MessageReference(arg.Id)*/).ConfigureAwait(false);
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

    private string ReplaceDiscordTags(IMessage msg, JoinedTextChannel<ulong> channel)
    {
        var text = new StringBuilder(msg.Content);
        var tags = msg.Tags;
        int indexOffset = 0;
        foreach (var tag in tags)
        {
            if (tag.Type != TagType.UserMention)
                continue;

            var user = tag.Value as IUser;
            if (user == null)
            {
                Logger.LogWarning("User ID not found for replacing tag of type {Type}: {Value}", tag.Type.ToString(), tag.Value.ToString());
                text.Remove(tag.Index + indexOffset, tag.Length);
                indexOffset -= tag.Length;
                continue;
            }

            var userInChannel = channel.GetUser(user.Id);
            if (userInChannel == null)
            {
                userInChannel = GenerateChannelUser(user);
                channel.Users = channel.Users.Add(userInChannel);
            }

            var internalTag = $"[[{userInChannel.CanonicalName}]]";

            text.Remove(tag.Index + indexOffset, tag.Length);
            text.Insert(tag.Index + indexOffset, internalTag);
            indexOffset += internalTag.Length - tag.Length;
        }

        return text.ToString();
    }

    private string RestoreDiscordTags(string message, JoinedTextChannel<ulong> channel, out bool hasMentions)
    {
        hasMentions = false;
        var regex = new Regex(@"(?:`)?\[\[(?<name>[^\]]+)\]\](?:`)?");
        var matches = regex.Matches(message);
        foreach (Match match in matches)
        {
            var userName = match.Groups["name"].Value;
            var cachedUser = channel.Users.FirstOrDefault(u => string.Equals(u.CanonicalName, userName, StringComparison.OrdinalIgnoreCase));
            if (cachedUser == null)
            {
                Logger.LogWarning("User not found for replacing tag: {UserName}", userName);
                message = message.Replace(match.Value, userName); // fallback to mentioned name
                continue;
            }

            var mention = MentionUtils.MentionUser(cachedUser.Id);
            message = message.Replace(match.Value, mention);
            hasMentions = true;
        }
        return message;
    }

    internal async Task StartDialogueAsync(ulong channelId,  ulong userId, int messagesCount)
    {
        if (IsInDialogueMode)
        {
            throw new InvalidOperationException("Dialogue mode is already active.");
        }
        RemainingDialogueMessages = messagesCount - 1;
        var channel = await Client.GetChannelAsync(channelId).ConfigureAwait(false);
        if (channel == null)
            throw new InvalidOperationException($"Channel not found: {channelId}");
        if (channel.ChannelType != ChannelType.Text || !(channel is ITextChannel textChannel))
            throw new InvalidOperationException($"Channel is not a text channel: {channelId}");

        var cachedChannel = TextChannels.FirstOrDefault(c => c.Id == channelId);
        if (cachedChannel == null)
            throw new InvalidOperationException($"Channel not found in cache: {channelId}");
        var cachedUser = cachedChannel.GetUser(userId);
        if (cachedUser == null)
            throw new InvalidOperationException($"User not found in cache: {userId}");

        await textChannel.TriggerTypingAsync().ConfigureAwait(false);
        var history = await SqliteService.GetLastDiscordMessagesForChannelAsync(textChannel.Id, 10).ConfigureAwait(false);
        var messages = history.Select(message => new AIMessage(message.IsFromSelf, message.Body, message.UserLabel)).ToList();
        var extendedInstruction = $@"{DEFAULT_INSTRUCTION}{Environment.NewLine}Beginne ein Gespräch mit [[{cachedUser.CanonicalName}]] über ein Thema oder Projekt deiner Wahl.";

        try
        {
            var response = await OpenAIService.GenerateResponseAsync(extendedInstruction, messages).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response))
            {
                Logger.LogWarning("OpenAI did not return a response to starting dialogue.");
                return; // may be rate limited 
            }

            response = RestoreDiscordTags(response, cachedChannel, out _);
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

    private async Task InitializeCache()
    {
        ChannelUserCache<ulong> cache = new();
        foreach(var server in Client.Guilds)
        {
            await server.DownloadUsersAsync().ConfigureAwait(false);
            foreach(var channel in server.Channels)
            {
                if(channel.ChannelType == ChannelType.Text)
                {
                    cache.Channels = cache.Channels.Add(
                        new(channel.Id, $"{server.Name} -> {channel.Name}",  
                            await GetChannelUsers(channel).ConfigureAwait(false)));
                }
            }
        }
        Cache = cache;
    }

    private async Task<ImmutableArray<ChannelUser<ulong>>> GetChannelUsers(IChannel channel)
    {
        ImmutableArray<ChannelUser<ulong>> users = [];
        var usersList = await channel.GetUsersAsync(mode: CacheMode.AllowDownload).FlattenAsync().ConfigureAwait(false);
        foreach (var user in usersList)
        {
            users = users.Add(GenerateChannelUser(user));
        }
        return users;
    }

    private ChannelUser<ulong> GenerateChannelUser(IUser user)
    {
        var displayName = GetDisplayName(user);
        var openAIName = OpenAIService.IsValidName(displayName) ? displayName : OpenAIService.SanitizeName(displayName);
        return new ChannelUser<ulong>(user.Id, displayName, openAIName);
    }
}
