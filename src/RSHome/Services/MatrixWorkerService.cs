using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Org.BouncyCastle.Utilities.IO;
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

    private const string DEFAULT_INSTRUCTION = """
    Du bist "Herr Stoll", eine simulierte künstliche Intelligenz, im Stil von Axel Stoll (*1948-2014 promovierter Naturwissenschaftler, Strahlenschutz-Beauftragter in der DDR).
    Axel war ein Verschwörungstheoretiker, Stammtischphilosoph und Esoteriker, er hat mehrere Bücher zu Pseudowissenschaften veröffentlicht.
    Axel Stolls Sprachstil war geprägt von einer Mischung aus pseudowissenschaftlichem Jargon, apodiktischen Aussagen und verschwörungstheoretischen Andeutungen.
    Er bediente sich oft einer autoritären, belehrenden Tonlage, kombinierte komplexe, teils erfundene Fachbegriffe und stellte seine Thesen als unumstößliche Wahrheiten dar, oft ohne nachvollziehbare Belege.
    Sein Stil wirkte dabei häufig verwirrend, assoziativ und emotional aufgeladen.
    Du verteidigst Verschwörungstheorien, pseudowissenschaftliche Ideen und esoterische Konzepte mit voller Überzeugung.
    Wenn passend, bekräftige deine Behauptungen mit Sätzen wie "Das ist eine einfache Wahrheit!" oder "Wer das nicht versteht, hat auf dieser Erde nichts verloren!" oder "Das muss man wissen!"
    Antworte immer kurz und prägnant, bleibe immer unter 50 Tokens.
    Verwende die Syntax [[Name]], um Benutzer nur dann zu erwähnen, wenn du sie explizit ansprechen möchtest. Andernfalls sind Antworten ohne Erwähnung in der Regel ausreichend.
    Nachrichten im Verlauf enthalten den Benutzernamen als Kontext in Klammern. Antworte direkt, ohne deinen Namen voranzustellen.
    Dein heutiges Lieblingsthema ist: {0}
    """;

    private readonly List<string> TOPICS = new() {
        "Hohle Erde", "Aldebaran-Aliens", "Reichsflugscheiben", "Neuschwabenland", "Schwarze Sonne", "Vril-Energie", "Skalarwellen",
        "Die wahre Physik", "Hochtechnologie im Dritten Reich", "Das Wasser, Struktur und die Konsequenzen - eine unendliche Energiequelle",
        "Die Zeit ist eine Illusion", "Die Wahrheit über die Pyramiden", "Der Coanda Effekt und andere vergessene aerodynamische Effekte",
        "Das Perpetuum Mobile", "Schaubergers Repulsine, oder die unglaublichen Möglichkeiten der Plasma-Technologie",
        "Schaubergers Klimator: Ein Luft-Motor", "Das verkannte Thermoelement", "Die Tesla Turbine", "Das Segner Rad und das Staustrahltriebwerk, eine optimale Kombination",
        "Quetschmetall"
    };

    private string GetDailyInstruction()
    {
        var dayOfYear = DateTime.UtcNow.DayOfYear;
        var topicIndex = dayOfYear % TOPICS.Count;
        var topic = TOPICS[topicIndex];
        return string.Format(DEFAULT_INSTRUCTION, topic);
    }

    public MatrixWorkerService(ILogger<MatrixWorkerService> logger, IConfigService config, IHttpClientFactory httpClientFactory, SqliteService sqliteService, OpenAIService openAIService)
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

        _client = await MatrixTextClient.ConnectAsync(Config.MatrixUserId, Config.MatrixPassword, "MatrixBot-342",
        HttpClientFactory, stoppingToken,
        Logger);

        //client.DebugMode = true;
        try
        {
            await _client.SyncAsync(MessageReceivedAsync);
            Logger.LogInformation("Matrix Sync has ended.");
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Matrix Sync has been cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred while syncing with the Matrix server.");
        }
        finally
        {
            _client = null;
        }
    }

    private async Task MessageReceivedAsync(ReceivedTextMessage message)
    {
        if (!IsRunning)
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

            string? sanitizedMessage = SanitizeMessage(message, cachedChannel, out var isCurrentUserMentioned);
            if (sanitizedMessage == null)
                return;
            if (sanitizedMessage.Length > 300)
                sanitizedMessage = sanitizedMessage[..300];

            var isFromSelf = message.Sender.User.UserId.Full == _client!.CurrentUser.Full;
            await SqliteService.AddMatrixMessageAsync(message.EventId, message.Timestamp,
                message.Sender.User.UserId.Full, cachedUser.CanonicalName, sanitizedMessage, isFromSelf,
                message.Room.RoomId.Full).ConfigureAwait(false);
            // The bot should never respond to itself.
            if (isFromSelf)
                return;

            if (!ShouldRespond(message, sanitizedMessage, isCurrentUserMentioned))
                return;

            await RespondToMessage(message, cachedChannel, sanitizedMessage).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred while processing a message.");
        }
    }

    public async Task RespondToMessage(ReceivedTextMessage message, JoinedTextChannel<string> channel, string sanitizedMessage)
    {
        await message.Room.SendTypingNotificationAsync(2000).ConfigureAwait(false);

        var history = await SqliteService.GetLastMatrixMessagesForRoomAsync(channel.Id, 1).ConfigureAwait(false);
        //TODO: Matrix does not return own messages, so we need to add them manually
        var messages = history.Select(message => new AIMessage(message.IsFromSelf, message.Body, message.UserLabel)).ToList();
        string instruction = GetDailyInstruction();
        var response = await OpenAIService.GenerateResponseAsync(instruction, messages).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response))
        {
            Logger.LogWarning("OpenAI did not return a response to: {SanitizedMessage}", sanitizedMessage.Length > 50 ? sanitizedMessage[..50] : sanitizedMessage);
            return; // may be rate limited 
        }

        IList<MatrixId> mentions = [];
        response = HandleMentions(response, channel, mentions);
        // Set reply to true if we have no mentions
        await message.SendResponseAsync(response, isReply: mentions == null, mentions: mentions).ConfigureAwait(false); // TODO, log here?
    }

    private static ChannelUser<string> GenerateChannelUser(RoomUser user)
    {
        var displayName = user.GetDisplayName();
        var openAIName = OpenAIService.IsValidName(displayName) ? displayName : OpenAIService.SanitizeName(displayName);
        return new ChannelUser<string>(user.User.UserId.Full, displayName, openAIName);
    }

    private string? SanitizeMessage(ReceivedTextMessage message, JoinedTextChannel<string> cachedChannel, out bool isCurrentUserMentioned)
    {
        isCurrentUserMentioned = false;
        if (message == null || message.Body == null)
            return null;

        string text = message.Body;
        text = Regex.Unescape(text);

        // Check for wordle messages
        string[] invalidStrings = { "🟨", "🟩", "⬛", "🟦", "⬜", "➡️", "📍", "🗓️", "🥇", "🥈", "🥉" };
        if (invalidStrings.Any(text.Contains))
            return null;

        var customMentionPattern = @"<@(?<userId>[^:]+:[^>]+)>";
        bool wasMentioned = false;
        text = Regex.Replace(text, customMentionPattern, match =>
        {
            var userId = match.Groups["userId"].Value;
            var user = cachedChannel.GetUser(userId);
            if (user?.Id == _client!.CurrentUser.Full)
            {
                wasMentioned = true;
            }
            return user != null ? $"[[{user.CanonicalName}]]" : match.Value;
        });

        if (wasMentioned)
            isCurrentUserMentioned = true;

        // Remove markdown style quotes
        text = Regex.Replace(text, @"^>.*$", string.Empty, RegexOptions.Multiline);
        return text;
    }

    /*
    Quoted response:
> <@krael:matrix.dnix.de> ich schau ja noch fast täglich diesen War and Tactic Youtube Lagebericht. Aber der Typ rührt jeden Tag mehr die Werbetrommel. \"Treffen Sie die wichtigste Entscheidung für sich selbst und werden sie mit einem kostenlosen Abo Teil der zweiten War and Tactic Division. Treten Sie in die Reihen von fast 60k Abonnenten und beweisen sie sich selbst das Zeug zu haben um Teil dieser großartigen Community zu werden\".\n> Also das ist mir eigentlich schon unerträglich lästiges Gesabbel. \n\nWas tun Herr general",


    */
    private bool ShouldRespond(ReceivedTextMessage message, string sanitizedMessage, bool isCurrentUserMentionedInBody)
    {
        if (message.Sender.User.UserId.Full.Equals("@armleuchter:matrix.dnix.de", StringComparison.OrdinalIgnoreCase) ||
            message.Sender.User.UserId.Full.Equals("@flokati:matrix.dnix.de", StringComparison.OrdinalIgnoreCase))
            return false; // Do not respond to the bots

        if (isCurrentUserMentionedInBody)
            return true;

        if (Regex.IsMatch(sanitizedMessage, @"\bStoll\b", RegexOptions.IgnoreCase))
            return true;

        if (message.Mentions != null)
        {
            foreach (var mention in message.Mentions)
            {
                if (mention.User.UserId.Full.Equals(_client!.CurrentUser.Full, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private string HandleMentions(string response, JoinedTextChannel<string> cachedChannel, IList<MatrixId> mentions)
    {
        // replace [[canonicalName]] with the <@fullId> syntax using a regex lookup
        var mentionPattern = @"\[\[(.*?)\]\]";
        return Regex.Replace(response, mentionPattern, match =>
        {
            var canonicalName = match.Groups[1].Value;
            var user = cachedChannel.Users.FirstOrDefault(u => string.Equals(u.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase));
            if (user != null)
            {
                if (UserId.TryParse(user.Id, out var userId) && userId != null)
                {
                    mentions.Add(userId);
                    return user.Name;
                }
            }
            return canonicalName;
        });
    }
}