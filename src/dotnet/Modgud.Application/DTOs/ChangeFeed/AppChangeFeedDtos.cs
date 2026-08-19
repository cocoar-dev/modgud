using System.Text.Json;

namespace Modgud.Application.DTOs.ChangeFeed;

public static class AppChangeFeedContract
{
    public const int Version = 1;
}

public sealed record AppScopedEntityDto(
    string EntityKind,
    string EntityId,
    int EntityVersion,
    JsonElement Payload);

public sealed record AppChangeFeedSnapshotDto(
    int ContractVersion,
    string AppId,
    string AppSlug,
    string ScopeVersion,
    string Cursor,
    IReadOnlyList<AppScopedEntityDto> Entities);

public sealed record AppChangeFeedMessageDto
{
    public required int ContractVersion { get; init; }
    public required string Kind { get; init; }
    public required string Cursor { get; init; }
    public required string ScopeVersion { get; init; }
    public string? ChangeKind { get; init; }
    public string? EntityKind { get; init; }
    public string? EntityId { get; init; }
    public int? EntityVersion { get; init; }
    public JsonElement? Payload { get; init; }
    public Guid? SourceEventId { get; init; }
    public DateTimeOffset? OriginatedAt { get; init; }
    public string? Reason { get; init; }
}

public sealed record AppChangeFeedReadDto(
    int ContractVersion,
    string ScopeVersion,
    IReadOnlyList<AppChangeFeedMessageDto> Messages,
    bool HasMore,
    bool FeedEnded = false);
