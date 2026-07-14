namespace LocalAgentTravelPlanner.Services.Conversations;

// ─────────────────────────────────────────────────────────────────────────────────────
// TurnUsage — per-turn cost / observability record.
//
// Current state: emitted as a Serilog structured event in TravelPlannerService
// (see LogTurnCompleted). Fields land as individual properties in console + rolling
// file sinks and are queryable without parsing the message text. Token counts are
// router-only today; per-agent LLM usage will be added once we put a token-capturing
// DelegatingChatClient middleware in front of the workflow's IChatClient.
//
// TODO: Persist to a TurnUsage table for cost dashboards and budget caps. Schema
// sketch below; add to the first migration script once a migrations framework
// (DbUp or FluentMigrator) is wired in.
//
//   CREATE TABLE TurnUsage (
//       Id              INTEGER PRIMARY KEY AUTOINCREMENT,
//       ConversationId  TEXT    NOT NULL,
//       TurnIndex       INTEGER NOT NULL,
//       Route           TEXT    NOT NULL,
//       AgentsRun       TEXT    NOT NULL,  -- comma-separated lowercase names
//       DurationMs      INTEGER NOT NULL,
//       Success         INTEGER NOT NULL,  -- 0/1
//       Error           TEXT    NULL,
//       Provider        TEXT    NULL,
//       Model           TEXT    NULL,
//       RouterInputTokens   INTEGER NULL,
//       RouterOutputTokens  INTEGER NULL,
//       AgentsInputTokens   INTEGER NULL,  -- populated once the token-capture middleware lands
//       AgentsOutputTokens  INTEGER NULL,
//       CreatedAt       TEXT    NOT NULL,
//       FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
//   );
//
//   CREATE INDEX IX_TurnUsage_Conv_TurnIndex ON TurnUsage(ConversationId, TurnIndex);
//   CREATE INDEX IX_TurnUsage_CreatedAt ON TurnUsage(CreatedAt DESC);
//
// To see today's logs locally:
//   tail -f Api/logs/api-*.log | grep "Conversation turn completed"
// ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// In-memory snapshot of one turn's observability data. Currently produced for log
/// emission only; persistence to a SQLite table is deferred until a migrations
/// framework is in place (see file-level comment).
/// </summary>
internal sealed record TurnUsageSnapshot(
    string ConversationId,
    TurnRoute Route,
    IReadOnlyList<string> AgentsRun,
    long DurationMs,
    bool Success,
    string? Error,
    int? RouterInputTokens,
    int? RouterOutputTokens);
