using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Services.Conversations;

/// <summary>
/// SQLite-backed conversation store. Schema is created idempotently on construction.
/// One open connection per operation — Microsoft.Data.Sqlite's connection pool keeps this
/// cheap, and short-lived connections sidestep the single-writer contention you get when
/// a long-lived handle holds the write lock.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Conversations (
            Id           TEXT PRIMARY KEY,
            Title        TEXT NULL,
            LatestPlan   TEXT NULL,
            CreatedAt    TEXT NOT NULL,
            LastActivity TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS IX_Conversations_LastActivity
            ON Conversations(LastActivity DESC);

        CREATE TABLE IF NOT EXISTS Messages (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ConversationId  TEXT NOT NULL,
            Role            TEXT NOT NULL,
            Content         TEXT NOT NULL,
            Position        INTEGER NOT NULL,
            CreatedAt       TEXT NOT NULL,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_Messages_Conv_Position
            ON Messages(ConversationId, Position);
        """;

    private readonly string _connectionString;

    public SqliteConversationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // WAL gives us reader concurrency during writes; foreign_keys is opt-in per-connection
        // but the PRAGMA on a fresh DB persists journal_mode for everyone.
        conn.Execute("PRAGMA journal_mode=WAL;");
        conn.Execute("PRAGMA foreign_keys=ON;");
        conn.Execute(SchemaSql);
    }

    public async Task<Conversation> CreateAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var conv = new Conversation
        {
            Id = Guid.NewGuid().ToString("n"),
            CreatedAt = now,
            LastActivity = now
        };

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Conversations (Id, Title, LatestPlan, CreatedAt, LastActivity) " +
            "VALUES (@Id, @Title, @LatestPlan, @CreatedAt, @LastActivity)",
            new
            {
                conv.Id,
                conv.Title,
                conv.LatestPlan,
                CreatedAt = FormatDate(conv.CreatedAt),
                LastActivity = FormatDate(conv.LastActivity)
            },
            cancellationToken: cancellationToken));

        return conv;
    }

    public async Task<Conversation?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);
        // Per-connection PRAGMA — needed for ON DELETE CASCADE on this handle
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON;", cancellationToken: cancellationToken));

        var row = await conn.QuerySingleOrDefaultAsync<ConvRow>(new CommandDefinition(
            "SELECT Id, Title, LatestPlan, CreatedAt, LastActivity FROM Conversations WHERE Id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));
        if (row == null) return null;

        var msgRows = await conn.QueryAsync<MsgRow>(new CommandDefinition(
            "SELECT Role, Content FROM Messages WHERE ConversationId = @Id ORDER BY Position",
            new { Id = id },
            cancellationToken: cancellationToken));

        var conv = new Conversation
        {
            Id = row.Id,
            Title = row.Title,
            LatestPlan = row.LatestPlan,
            CreatedAt = ParseDate(row.CreatedAt),
            LastActivity = ParseDate(row.LastActivity)
        };

        foreach (var m in msgRows)
            conv.History.Add(new ChatMessage(ParseRole(m.Role), m.Content));

        return conv;
    }

    public async Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        conversation.LastActivity = DateTime.UtcNow;

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON;", cancellationToken: cancellationToken));

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE Conversations SET Title=@Title, LatestPlan=@LatestPlan, LastActivity=@LastActivity WHERE Id=@Id",
            new
            {
                conversation.Title,
                conversation.LatestPlan,
                LastActivity = FormatDate(conversation.LastActivity),
                conversation.Id
            },
            transaction: tx,
            cancellationToken: cancellationToken));

        if (updated == 0)
        {
            // Conversation was deleted between Get and Update — surface clearly so the caller
            // can decide whether to recreate or just drop the turn.
            await tx.RollbackAsync(cancellationToken);
            throw new KeyNotFoundException($"Conversation '{conversation.Id}' no longer exists.");
        }

        // Replace-all semantics for history. With chat lengths in the dozens this is fine; the
        // alternative (diffing tail-appends) buys negligible perf and a class of edge-case bugs.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Messages WHERE ConversationId = @Id",
            new { conversation.Id },
            transaction: tx,
            cancellationToken: cancellationToken));

        if (conversation.History.Count > 0)
        {
            var createdAt = FormatDate(DateTime.UtcNow);
            var rows = new List<object>(conversation.History.Count);
            for (var i = 0; i < conversation.History.Count; i++)
            {
                var msg = conversation.History[i];
                rows.Add(new
                {
                    ConversationId = conversation.Id,
                    Role = RoleToString(msg.Role),
                    Content = msg.Text ?? string.Empty,
                    Position = i,
                    CreatedAt = createdAt
                });
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO Messages (ConversationId, Role, Content, Position, CreatedAt) " +
                "VALUES (@ConversationId, @Role, @Content, @Position, @CreatedAt)",
                rows,
                transaction: tx,
                cancellationToken: cancellationToken));
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON;", cancellationToken: cancellationToken));

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Conversations WHERE Id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            "SELECT Id, Title, LastActivity FROM Conversations ORDER BY LastActivity DESC",
            cancellationToken: cancellationToken));

        return rows
            .Select(r => new ConversationSummary(r.Id, r.Title, ParseDate(r.LastActivity)))
            .ToList();
    }

    private SqliteConnection OpenConnection() => new(_connectionString);

    private static string FormatDate(DateTime dt)
        => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string s)
        => DateTime.SpecifyKind(
            DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeKind.Utc);

    private static string RoleToString(ChatRole role)
    {
        if (role == ChatRole.Assistant) return "assistant";
        if (role == ChatRole.System) return "system";
        return "user";
    }

    private static ChatRole ParseRole(string s) => s switch
    {
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        _ => ChatRole.User
    };

    // Row DTOs for Dapper mapping — kept private to this file
    private sealed record ConvRow(string Id, string? Title, string? LatestPlan, string CreatedAt, string LastActivity);
    private sealed record MsgRow(string Role, string Content);
    private sealed record SummaryRow(string Id, string? Title, string LastActivity);
}
