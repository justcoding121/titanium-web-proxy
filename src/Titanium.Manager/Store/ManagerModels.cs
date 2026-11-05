namespace Titanium.Manager.Store;

internal sealed record FleetRow(long Id, string Name, string LabelsJson, DateTimeOffset CreatedAt);

internal sealed record AgentRow(
    long Id,
    long FleetId,
    string NodeId,
    string LabelsJson,
    DateTimeOffset? LastSeen,
    string? ConfigHash,
    string? HealthJson,
    string Status,
    long? DesiredRevisionId);

internal sealed record RevisionRow(
    long Id,
    long FleetId,
    string ContentHash,
    string YamlText,
    string Author,
    long? ParentId,
    DateTimeOffset CreatedAt);

internal sealed record RolloutRow(
    long Id,
    long FleetId,
    long RevisionId,
    long? PreviousRevisionId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PauseReason);

internal sealed record RolloutStepRow(
    long Id,
    long RolloutId,
    int Ordinal,
    string LabelSelectorJson,
    int MaxUnavailable,
    int BakeSeconds,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>One wave in a staged publish. Empty <see cref="MatchLabels"/> matches all agents.</summary>
internal sealed record PublishWaveSpec(
    IReadOnlyDictionary<string, string> MatchLabels,
    int MaxUnavailable,
    int BakeSeconds);

internal sealed record AgentTokenRow(
    long Id,
    long ClusterId,
    string Name,
    string TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

internal sealed record AdminTokenRow(
    long Id,
    string Name,
    string TokenHash,
    string Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

internal sealed record AuditRow(
    long Id,
    DateTimeOffset Time,
    string Actor,
    string Action,
    string Target,
    string Result,
    string? Detail);

internal sealed record PublishedConfig(long RevisionId, string ContentHash, string YamlText);
