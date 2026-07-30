using System.Globalization;
using System.Text.Json;
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

        CREATE TABLE IF NOT EXISTS Turns (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ConversationId  TEXT NOT NULL,
            TurnIndex       INTEGER NOT NULL,
            Route           TEXT NOT NULL,
            AgentsRun       TEXT NOT NULL,
            CreatedAt       TEXT NOT NULL,
            DurationMs      INTEGER NULL,
            Provider        TEXT NULL,
            Model           TEXT NULL,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_Turns_Conv_Index
            ON Turns(ConversationId, TurnIndex);

        CREATE TABLE IF NOT EXISTS TurnAgentContent (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ConversationId  TEXT NOT NULL,
            TurnIndex       INTEGER NOT NULL,
            Agent           TEXT NOT NULL,
            Content         TEXT NOT NULL,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_TurnAgentContent_Conv_Turn
            ON TurnAgentContent(ConversationId, TurnIndex);
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

        // Backwards-compat: existing DBs may have older Turns table without newer columns.
        // SQLite has no "ADD COLUMN IF NOT EXISTS" — swallow the "duplicate column" error.
        TryAddColumn(conn, "Turns", "DurationMs", "INTEGER");
        TryAddColumn(conn, "Turns", "Provider", "TEXT");
        TryAddColumn(conn, "Turns", "Model", "TEXT");
        TryAddColumn(conn, "Turns", "ChangeSummary", "TEXT");
        TryAddColumn(conn, "Turns", "InputTokens", "INTEGER");
        TryAddColumn(conn, "Turns", "OutputTokens", "INTEGER");

        // Human-in-the-loop columns on Conversations.
        // RequireApproval: opt-in flag (defaults 0 = off for backwards compatibility).
        // PendingDecisionJson: null unless a turn is paused awaiting user decision.
        TryAddColumn(conn, "Conversations", "RequireApproval", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(conn, "Conversations", "PendingDecisionJson", "TEXT");
    }

    private static void TryAddColumn(SqliteConnection conn, string table, string col, string type)
    {
        try { conn.Execute($"ALTER TABLE {table} ADD COLUMN {col} {type} NULL"); }
        catch (SqliteException) { /* column already exists */ }
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
            "INSERT INTO Conversations (Id, Title, LatestPlan, CreatedAt, LastActivity, RequireApproval, PendingDecisionJson) " +
            "VALUES (@Id, @Title, @LatestPlan, @CreatedAt, @LastActivity, 0, NULL)",
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
            "SELECT Id, Title, LatestPlan, CreatedAt, LastActivity, RequireApproval, PendingDecisionJson FROM Conversations WHERE Id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));
        if (row == null) return null;

        var msgRows = await conn.QueryAsync<MsgRow>(new CommandDefinition(
            "SELECT Role, Content FROM Messages WHERE ConversationId = @Id ORDER BY Position",
            new { Id = id },
            cancellationToken: cancellationToken));

        var turnRows = await conn.QueryAsync<TurnRow>(new CommandDefinition(
            "SELECT TurnIndex, Route, AgentsRun, CreatedAt, DurationMs, Provider, Model, ChangeSummary, InputTokens, OutputTokens FROM Turns WHERE ConversationId = @Id ORDER BY TurnIndex",
            new { Id = id },
            cancellationToken: cancellationToken));

        var agentContentRows = await conn.QueryAsync<AgentContentRow>(new CommandDefinition(
            "SELECT TurnIndex, Agent, Content FROM TurnAgentContent WHERE ConversationId = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));

        // Group agent content by TurnIndex for O(1) attach below
        var contentByTurn = agentContentRows
            .GroupBy(r => (int)r.TurnIndex)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string>)g.ToDictionary(
                    r => r.Agent,
                    r => r.Content,
                    StringComparer.OrdinalIgnoreCase));

        var conv = new Conversation
        {
            Id = row.Id,
            Title = row.Title,
            LatestPlan = row.LatestPlan,
            CreatedAt = ParseDate(row.CreatedAt),
            LastActivity = ParseDate(row.LastActivity),
            RequireApproval = row.RequireApproval != 0,
            PendingDecision = DeserializePending(row.PendingDecisionJson)
        };

        foreach (var m in msgRows)
            conv.History.Add(new ChatMessage(ParseRole(m.Role), m.Content));

        foreach (var t in turnRows)
        {
            var agents = string.IsNullOrEmpty(t.AgentsRun)
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : t.AgentsRun.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var route = Enum.TryParse<TurnRoute>(t.Route, ignoreCase: true, out var r) ? r : TurnRoute.Full;
            contentByTurn.TryGetValue((int)t.TurnIndex, out var outputs);
            conv.Turns.Add(new TurnMetadata(
                (int)t.TurnIndex,
                route,
                agents,
                ParseDate(t.CreatedAt),
                outputs,
                t.DurationMs,
                t.Provider,
                t.Model,
                t.ChangeSummary,
                t.InputTokens,
                t.OutputTokens));
        }

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
            "UPDATE Conversations SET Title=@Title, LatestPlan=@LatestPlan, LastActivity=@LastActivity, " +
            "RequireApproval=@RequireApproval, PendingDecisionJson=@PendingDecisionJson WHERE Id=@Id",
            new
            {
                conversation.Title,
                conversation.LatestPlan,
                LastActivity = FormatDate(conversation.LastActivity),
                RequireApproval = conversation.RequireApproval ? 1 : 0,
                PendingDecisionJson = SerializePending(conversation.PendingDecision),
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

        // Replace-all for Turns too (matches Messages semantics)
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Turns WHERE ConversationId = @Id",
            new { conversation.Id },
            transaction: tx,
            cancellationToken: cancellationToken));

        if (conversation.Turns.Count > 0)
        {
            var turnRows = new List<object>(conversation.Turns.Count);
            foreach (var t in conversation.Turns)
            {
                turnRows.Add(new
                {
                    ConversationId = conversation.Id,
                    TurnIndex = t.TurnIndex,
                    Route = t.Route.ToString(),
                    AgentsRun = string.Join(",", t.AgentsRun),
                    CreatedAt = FormatDate(t.CreatedAt),
                    DurationMs = t.DurationMs,
                    Provider = t.Provider,
                    Model = t.Model,
                    ChangeSummary = t.ChangeSummary,
                    InputTokens = t.InputTokens,
                    OutputTokens = t.OutputTokens
                });
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO Turns (ConversationId, TurnIndex, Route, AgentsRun, CreatedAt, DurationMs, Provider, Model, ChangeSummary, InputTokens, OutputTokens) " +
                "VALUES (@ConversationId, @TurnIndex, @Route, @AgentsRun, @CreatedAt, @DurationMs, @Provider, @Model, @ChangeSummary, @InputTokens, @OutputTokens)",
                turnRows,
                transaction: tx,
                cancellationToken: cancellationToken));
        }

        // Replace-all for TurnAgentContent — same semantics
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM TurnAgentContent WHERE ConversationId = @Id",
            new { conversation.Id },
            transaction: tx,
            cancellationToken: cancellationToken));

        var contentRows = new List<object>();
        foreach (var t in conversation.Turns)
        {
            if (t.AgentOutputs == null) continue;
            foreach (var (agent, content) in t.AgentOutputs)
            {
                if (string.IsNullOrEmpty(content)) continue;
                contentRows.Add(new
                {
                    ConversationId = conversation.Id,
                    TurnIndex = t.TurnIndex,
                    Agent = agent,
                    Content = content
                });
            }
        }
        if (contentRows.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO TurnAgentContent (ConversationId, TurnIndex, Agent, Content) " +
                "VALUES (@ConversationId, @TurnIndex, @Agent, @Content)",
                contentRows,
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

    // Pending-decision blob is small (~10-30 KB with three agent outputs) and only read on
    // conversation load; JSON keeps schema evolution painless vs a normalized side table.
    private static readonly JsonSerializerOptions PendingJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string? SerializePending(PendingDecision? pd)
        => pd == null ? null : JsonSerializer.Serialize(pd, PendingJsonOptions);

    private static PendingDecision? DeserializePending(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<PendingDecision>(json, PendingJsonOptions);

    // Row DTOs for Dapper mapping — kept private to this file
    private sealed record ConvRow(string Id, string? Title, string? LatestPlan, string CreatedAt, string LastActivity, long RequireApproval, string? PendingDecisionJson);
    private sealed record MsgRow(string Role, string Content);
    private sealed record SummaryRow(string Id, string? Title, string LastActivity);
    private sealed record TurnRow(long TurnIndex, string Route, string AgentsRun, string CreatedAt, long? DurationMs, string? Provider, string? Model, string? ChangeSummary, long? InputTokens, long? OutputTokens);
    private sealed record AgentContentRow(long TurnIndex, string Agent, string Content);
}
