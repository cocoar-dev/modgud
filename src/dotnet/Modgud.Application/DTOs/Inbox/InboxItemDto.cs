using System.Text.Json;

namespace Modgud.Application.DTOs.Inbox;

public class InboxItemDto
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public required string Severity { get; set; }
    public required string TitleKey { get; set; }
    public string? BodyKey { get; set; }
    public JsonElement? Params { get; set; }
    public string? Link { get; set; }
    public string? SourceType { get; set; }
    public string? SourceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? DismissedAt { get; set; }
    public DateTime? SnoozeUntil { get; set; }

    // Static descriptor data hydrated from the registry — handy for the client
    // so it doesn't need to mirror the registry. Deliberately on the wire each
    // time (small payload, simpler client).
    public required string Persistence { get; set; }
    public bool Actionable { get; set; }
    public required string Icon { get; set; }
}
