using System.Buffers.Binary;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.WebUtilities;
using Modgud.Application.DTOs.ChangeFeed;
using Modgud.Authorization.Apps;
using Modgud.Infrastructure.ChangeFeed;

namespace Modgud.Api.Features.ChangeFeed;

public sealed class AppChangeFeedQueryService(IQuerySession session)
{
    public async Task<AppChangeFeedQueryResult<AppChangeFeedSnapshotDto>> SnapshotAsync(
        Guid appId,
        CancellationToken cancellationToken)
    {
        var state = await session.LoadAsync<AppChangeFeedState>(appId, cancellationToken);
        if (state is null)
            return AppChangeFeedQueryResult<AppChangeFeedSnapshotDto>.Fail(
                "FeedInitializing", "The change-feed projection has not initialized this Application yet.", 409);
        if (!state.Enabled)
            return AppChangeFeedQueryResult<AppChangeFeedSnapshotDto>.Fail(
                "FeedDisabled", "The change feed is disabled for this Application.", 409);

        var app = await session.LoadAsync<App>(appId, cancellationToken);
        if (app is null || app.IsDeleted)
            return AppChangeFeedQueryResult<AppChangeFeedSnapshotDto>.Fail(
                "ApplicationNotFound", "Application not found.", 404);

        var rows = await session.Query<AppChangeFeedEntityState>()
            .Where(x => x.AppId == appId)
            .OrderBy(x => x.EntityKind)
            .ThenBy(x => x.EntityId)
            .ToListAsync(cancellationToken);
        var entities = rows.Select(row => new AppScopedEntityDto(
                row.EntityKind,
                ShortGuid.Encode(row.EntityId),
                EntityVersion: 1,
                ParsePayload(row.PayloadJson)))
            .ToList();

        return AppChangeFeedQueryResult<AppChangeFeedSnapshotDto>.Ok(new AppChangeFeedSnapshotDto(
            AppChangeFeedContract.Version,
            ShortGuid.Encode(appId),
            app.Slug,
            state.ScopeVersion,
            AppChangeFeedCursor.EncodeCheckpoint(state),
            entities));
    }

    public async Task<AppChangeFeedQueryResult<AppChangeFeedReadDto>> ReadAsync(
        Guid appId,
        string cursorText,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 500);
        var state = await session.LoadAsync<AppChangeFeedState>(appId, cancellationToken);
        if (state is null)
            return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Fail(
                "FeedInitializing", "The change-feed projection has not initialized this Application yet.", 409);

        if (!AppChangeFeedCursor.TryDecode(cursorText, out var cursor))
            return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Fail(
                "InvalidCursor", "The cursor is malformed or uses an unsupported version.", 400);
        if (cursor.AppId != appId)
            return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Fail(
                "InvalidCursor", "The cursor belongs to a different Application.", 400);
        if (cursor.Generation != state.Generation)
            return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Fail(
                "ScopeChanged", "The Application scope changed; take a new full snapshot.", 409);
        if (Compare(cursor.Sequence, cursor.Ordinal,
                state.RetentionFloorSequence, state.RetentionFloorOrdinal) <= 0
            && (state.RetentionFloorSequence > 0 || state.RetentionFloorOrdinal >= 0))
        {
            return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Fail(
                "CursorTooOld", "The cursor is outside the retained resume window; take a new full snapshot.", 409);
        }
        if (cursor.Sequence > state.LastProcessedSequence)
            return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Fail(
                "InvalidCursor", "The cursor points beyond the feed checkpoint.", 400);

        var rows = (await session.Query<AppChangeFeedEntry>()
            .Where(x => x.AppId == appId
                        && x.Generation == state.Generation
                        && (x.SourceSequence > cursor.Sequence
                            || (x.SourceSequence == cursor.Sequence && x.Ordinal > cursor.Ordinal)))
            .OrderBy(x => x.SourceSequence)
            .ThenBy(x => x.Ordinal)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)).ToList();

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var messages = rows.Select(MapEntry).ToList();

        if (!hasMore)
        {
            var lastSequence = rows.Count == 0 ? cursor.Sequence : rows[^1].SourceSequence;
            var lastOrdinal = rows.Count == 0 ? cursor.Ordinal : rows[^1].Ordinal;
            if (Compare(lastSequence, lastOrdinal, state.LastProcessedSequence, int.MaxValue) < 0)
            {
                messages.Add(new AppChangeFeedMessageDto
                {
                    ContractVersion = AppChangeFeedContract.Version,
                    Kind = "Checkpoint",
                    Cursor = AppChangeFeedCursor.EncodeCheckpoint(state),
                    ScopeVersion = state.ScopeVersion,
                });
            }
        }

        if (!state.Enabled && messages.Count == 0)
            return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Fail(
                "FeedDisabled", "The change feed is disabled for this Application.", 409);

        return AppChangeFeedQueryResult<AppChangeFeedReadDto>.Ok(new AppChangeFeedReadDto(
            AppChangeFeedContract.Version,
            state.ScopeVersion,
            messages,
            hasMore,
            FeedEnded: !state.Enabled && !hasMore));
    }

    private static AppChangeFeedMessageDto MapEntry(AppChangeFeedEntry entry) => new()
    {
        ContractVersion = AppChangeFeedContract.Version,
        Kind = entry.ChangeKind switch
        {
            AppChangeKinds.ScopeChanged => "ResetRequired",
            AppChangeKinds.FeedDisabled => "FeedEnded",
            _ => "Change",
        },
        Cursor = AppChangeFeedCursor.Encode(
            entry.AppId, entry.Generation, entry.SourceSequence, entry.Ordinal),
        ScopeVersion = entry.ScopeVersion,
        ChangeKind = entry.ChangeKind,
        EntityKind = entry.EntityKind,
        EntityId = entry.EntityId is { } id ? ShortGuid.Encode(id) : null,
        EntityVersion = entry.EntityId.HasValue ? 1 : null,
        Payload = entry.PayloadJson is null ? null : ParsePayload(entry.PayloadJson),
        SourceEventId = entry.SourceEventId,
        OriginatedAt = entry.OriginatedAt,
        Reason = entry.Reason,
    };

    private static JsonElement ParsePayload(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static int Compare(long leftSequence, int leftOrdinal, long rightSequence, int rightOrdinal)
    {
        var sequence = leftSequence.CompareTo(rightSequence);
        return sequence != 0 ? sequence : leftOrdinal.CompareTo(rightOrdinal);
    }
}

public sealed record AppChangeFeedQueryResult<T>(T? Value, AppChangeFeedQueryError? Error)
{
    public bool IsSuccess => Error is null;
    public static AppChangeFeedQueryResult<T> Ok(T value) => new(value, null);
    public static AppChangeFeedQueryResult<T> Fail(string code, string detail, int statusCode) =>
        new(default, new AppChangeFeedQueryError(code, detail, statusCode));
}

public sealed record AppChangeFeedQueryError(string Code, string Detail, int StatusCode);

internal readonly record struct AppChangeFeedCursorValue(
    Guid AppId,
    int Generation,
    long Sequence,
    int Ordinal);

internal static class AppChangeFeedCursor
{
    private const byte Version = 1;
    private const int Size = 1 + 16 + 4 + 8 + 4;

    public static string EncodeCheckpoint(AppChangeFeedState state) =>
        Encode(state.Id, state.Generation, state.LastProcessedSequence, int.MaxValue);

    public static string Encode(Guid appId, int generation, long sequence, int ordinal)
    {
        Span<byte> bytes = stackalloc byte[Size];
        bytes[0] = Version;
        appId.TryWriteBytes(bytes[1..17]);
        BinaryPrimitives.WriteInt32BigEndian(bytes[17..21], generation);
        BinaryPrimitives.WriteInt64BigEndian(bytes[21..29], sequence);
        BinaryPrimitives.WriteInt32BigEndian(bytes[29..33], ordinal);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    public static bool TryDecode(string? text, out AppChangeFeedCursorValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(text);
            if (bytes.Length != Size || bytes[0] != Version) return false;
            value = new AppChangeFeedCursorValue(
                new Guid(bytes.AsSpan(1, 16)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(17, 4)),
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(21, 8)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(29, 4)));
            return value.Generation > 0 && value.Sequence >= 0 && value.Ordinal >= -1;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
