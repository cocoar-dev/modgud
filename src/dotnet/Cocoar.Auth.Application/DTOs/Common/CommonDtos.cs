namespace Cocoar.Auth.Application.DTOs.Common;

/// <summary>
/// Standard error response DTO.
/// </summary>
public record ErrorDto
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, string[]>? Errors { get; init; }
}

/// <summary>
/// Standard pagination request.
/// </summary>
public record PaginationRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Search { get; init; }
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }
}
