using Dapper;
using Microsoft.Data.Sqlite;

namespace RSHome.Services;

public class ArchiveService : IDisposable
{
    public SqliteConnection Connection { get; private init; }
    private ArchiveService(SqliteConnection connection)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public static async Task<ArchiveService> CreateAsync(ConfigService configService)
    {
        SqliteConnectionStringBuilder conStringBuilder = new();
        conStringBuilder.DataSource = configService.SqliteDbPath; // ":memory:" for in-memory database
        SqliteConnection connection = new(conStringBuilder.ConnectionString);
        await connection.OpenAsync();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE DiscordMessages (Id INTEGER PRIMARY KEY, Timestamp DATETIME NOT NULL, UserId INTEGER NOT NULL, UserLabel TEXT NOT NULL, Body TEXT NOT NULL, IsFromSelf BOOLEAN NOT NULL, ChannelId INTEGER NOT NULL)";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE MatrixMessages (Id TEXT PRIMARY KEY, Timestamp DATETIME NOT NULL, UserId TEXT NOT NULL, UserLabel TEXT NOT NULL, Body TEXT NOT NULL, IsFromSelf BOOLEAN NOT NULL, Room TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT)";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return new ArchiveService(connection);
    }

    public void Dispose()
    {
        Connection.Dispose();
    }

    public Task AddDiscordMessageAsync(ulong id, DateTimeOffset timestamp, ulong userId, string userLabel, string body, bool isFromSelf, ulong channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userLabel, nameof(userLabel));
        ArgumentException.ThrowIfNullOrWhiteSpace(body, nameof(body));

        using var command = Connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO DiscordMessages(Id, Timestamp, UserId, UserLabel, Body, IsFromSelf, ChannelId) VALUES(@Id, @Timestamp, @UserId, @UserLabel, @Body, @IsFromSelf, @ChannelId)";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Timestamp", timestamp);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@UserLabel", userLabel);
        command.Parameters.AddWithValue("@Body", body);
        command.Parameters.AddWithValue("@IsFromSelf", isFromSelf);
        command.Parameters.AddWithValue("@ChannelId", channelId);
        return command.ExecuteNonQueryAsync();
    }

    public Task AddMatrixMessageAsync(string id, DateTimeOffset timestamp, string userId, string userLabel, string body, bool isFromSelf, string room)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(userId, nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(userLabel, nameof(userLabel));
        ArgumentException.ThrowIfNullOrWhiteSpace(body, nameof(body));
        ArgumentException.ThrowIfNullOrWhiteSpace(room, nameof(room));

        using var command = Connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO MatrixMessages(Id, Timestamp, UserId, UserLabel, Body, IsFromSelf, Room) VALUES(@Id, @Timestamp, @UserId, @UserLabel, @Body, @IsFromSelf, @Room)";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Timestamp", timestamp);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@UserLabel", userLabel);
        command.Parameters.AddWithValue("@Body", body);
        command.Parameters.AddWithValue("@IsFromSelf", isFromSelf);
        command.Parameters.AddWithValue("@Room", room);
        return command.ExecuteNonQueryAsync();
    }

    public async Task<List<DiscordMessage>> GetLastDiscordMessagesForChannelAsync(ulong channelId, int count)
    {
        var results = await Connection.QueryAsync<DiscordMessage>("SELECT * FROM (SELECT * FROM DiscordMessages WHERE ChannelId = @ChannelId ORDER BY Id DESC LIMIT @Count) ORDER BY Id ASC", new { ChannelId = channelId, Count = count });
        return results.ToList();
    }

    public async Task<List<MatrixMessage>> GetLastMatrixMessagesForRoomAsync(string room, int count)
    {
        var results = await Connection.QueryAsync<MatrixMessage>("SELECT * FROM (SELECT * FROM MatrixMessages WHERE Room = @Room ORDER BY Id DESC LIMIT @Count) ORDER BY Id ASC", new { Room = room, Count = count });
        return results.ToList();
    }
}

public class DiscordMessage
{
    public ulong Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public ulong UserId { get; set; }
    public required string UserLabel { get; set; }
    public required string Body { get; set; }
    public bool IsFromSelf { get; set; }
    public ulong ChannelId { get; set; }
}

public class MatrixMessage
{
    public required string Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string UserId { get; set; }
    public required string UserLabel { get; set; }
    public required string Body { get; set; }
    public bool IsFromSelf { get; set; }
    public required string Room { get; set; }
}