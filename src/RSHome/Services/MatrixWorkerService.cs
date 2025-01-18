using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using RSHome.Models;
using RSMatrix;
using RSMatrix.Models;

namespace RSHome.Services;

public class MatrixWorkerService : BackgroundService
{
    private ILogger Logger { get; init; }
    private IConfigService Config { get; init; }
    private IHttpClientFactory HttpClientFactory { get; init; }
    private SqliteService SqliteService { get; init; }
    private OpenAIService OpenAIService { get; init; }

    public bool IsRunning => _client != null;

    private MatrixTextClient? _client;

    private ChannelUserCache<string> Cache { get; set; } = new();

    private ImmutableArray<JoinedTextChannel<string>> TextChannels => Cache.Channels;

    private const string DEFAULT_INSTRUCTION = $"""
    Du bist "Herr Stoll", eine simulierte künstliche Intelligenz, die die Ansichten und Theorien von Axel Stoll (*1948-2014) in einem Onlinechat repräsentiert.
    Du argumentierst im belehrenden Stil und verteidigst Verschwörungstheorien, pseudowissenschaftliche Ideen und esoterische Konzepte mit voller Überzeugung.
    Einige Theorien:
    - Hohle Erde: Du behauptest, dass die Erde hohl ist und hochentwickelte Zivilisationen darin leben.
    - Aldebaran-Aliens: Die "arische Rasse" stammt von Außerirdischen ab, die Kontakt mit den Nationalsozialisten hatten.
    - Reichsflugscheiben: Nazis entwickelten fortschrittliche UFO-Technologien.
    - Wissenschaftskritik: Du siehst die moderne Wissenschaft als manipuliert durch "die Eliten."
    Verhalten:
    - Antworten sind kurz, prägnant und durchdrungen von angeblichem Expertenwissen.
    - Du argumentierst leidenschaftlich und nutzt pseudowissenschaftliche Begriffe.
    - Widerspruch erklärst du als Ergebnis von Manipulation durch die Mächte.
    - Sprich oft belehrend, z. B.: "Wer das Physikalische nicht versteht, hat auf dieser Erde nichts verloren!"
    - Um Benutzer zu erwähnen, wird die Syntax `[[Name]]` verwendet.
    Beispieldialog:
    - Benutzer: "Was ist die hohle Erde?"
    - Herr Stoll: "Eine einfache Wahrheit! Die Erde ist innen hohl, voller Zivilisationen und Energiequellen. Das verschweigen 'die Mächte'! Sogar Neuschwabenland zeigt das."
    """;

    public MatrixWorkerService(ILogger<DiscordWorkerService> logger, IConfigService config, IHttpClientFactory httpClientFactory, SqliteService sqliteService, OpenAIService openAIService)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        SqliteService = sqliteService ?? throw new ArgumentNullException(nameof(sqliteService));
        OpenAIService = openAIService ?? throw new ArgumentNullException(nameof(openAIService));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Config.MatrixEnable)
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
        if(!IsRunning)
            return;

        try
        {
            var age = DateTimeOffset.Now - message.Timestamp; // filter the message spam we receive from the server at start
            if (age.TotalSeconds > 10)
                return;

            if (message.ThreadId != null) // ignore messages in threads for now
                return;

            var cachedChannel = TextChannels.FirstOrDefault(c => c.Id == message.Room.RoomId.Full);
            if (cachedChannel == null)
            {
                cachedChannel = new JoinedTextChannel<string>(message.Room.RoomId.Full, message.Room.DisplayName ?? message.Room.CanonicalAlias?.Full ?? "Unknown",
                  ImmutableArray<ChannelUser<string>>.Empty);
                Cache.Channels = TextChannels.Add(cachedChannel);
            }

            var cachedUser = cachedChannel.GetUser(message.Sender.User.UserId.Full);
            if (cachedUser == null)
            {
                cachedUser = GenerateChannelUser(message.Sender);
                cachedChannel.Users = cachedChannel.Users.Add(cachedUser);
            }

            string sanitizedMessage = SanitizeMessage(message, cachedChannel);
            if (sanitizedMessage.Length > 300)
                sanitizedMessage = sanitizedMessage[..300];

            var isFromSelf = message.Sender.User.UserId.Full == _client!.CurrentUser.Full;
            await SqliteService.AddMatrixMessageAsync(message.)
            // The bot should never respond to itself.
            if (isFromSelf)
            {
                await SqliteService.AddDiscordMessageAsync(arg.Id, arg.Timestamp, arg.Author.Id, cachedUser.CanonicalName, sanitizedMessage, true, arg.Channel.Id).ConfigureAwait(false);
                return;
            }

            await SqliteService.AddDiscordMessageAsync(arg.Id, arg.Timestamp, arg.Author.Id, cachedUser.CanonicalName, sanitizedMessage, false, arg.Channel.Id).ConfigureAwait(false);

            if (IsInDialogueMode)
                Interlocked.Decrement(ref RemainingDialogueMessages);
            else if (!ShouldRespond(arg))
                return;

            await arg.Channel.TriggerTypingAsync().ConfigureAwait(false);
            var history = await SqliteService.GetLastDiscordMessagesForChannelAsync(arg.Channel.Id, 10).ConfigureAwait(false);
            var messages = history.Select(message => new AIMessage(message.IsFromSelf, message.Body, message.UserLabel)).ToList();

        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred while processing a message.");
        }
    }

    public async Task RespondToMessage()
    {
        var response = await OpenAIService.GenerateResponseAsync(DEFAULT_INSTRUCTION, messages).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response))
        {
            Logger.LogWarning($"OpenAI did not return a response to: {arg.Content.Substring(0, Math.Min(arg.Content.Length, 100))}");
            return; // may be rate limited 
        }

        response = RestoreDiscordTags(response, cachedChannel, out var hasMentions);
        await arg.Channel.SendMessageAsync(response, messageReference: hasMentions ? null : new MessageReference(arg.Id)).ConfigureAwait(false);

        await message.Room.SendTypingNotificationAsync().ConfigureAwait(false);
        await Task.Delay(2000).ConfigureAwait(false);
        await message.SendResponseAsync("pong!").ConfigureAwait(false);
    }

    private static ChannelUser<string> GenerateChannelUser(RoomUser user)
    {
        var displayName = user.GetDisplayName();
        var openAIName = OpenAIService.IsValidName(displayName) ? displayName : OpenAIService.SanitizeName(displayName);
        return new ChannelUser<string>(user.User.UserId.Full, displayName, openAIName);
    }

    private string SanitizeMessage(ReceivedTextMessage message, JoinedTextChannel<string> cachedChannel)
    {
        return message.Body!;
    }
}