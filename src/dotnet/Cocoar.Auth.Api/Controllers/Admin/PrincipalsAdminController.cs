using Cocoar.Auth.Api.Authorization;
using Cocoar.Auth.Domain.Principals;
using Cocoar.Auth.Infrastructure.Authorization;
using Cocoar.Auth.Infrastructure.Persistence;
using Marten;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers.Admin;

/// <summary>
/// Cross-type lookup for principals (Persons + Groups). Backs the member-picker
/// in the AuthorizationGroup edit UI, plus any other admin surface that needs
/// to autocomplete a principal id without caring whether it's a user or a group.
/// </summary>
[Route("api/admin/principals")]
[RequiresAbacPermission("authorization-group:read")]
public class PrincipalsAdminController : ApiControllerBase
{
    private readonly ITenantSessionFactory _sessionFactory;

    public PrincipalsAdminController(ITenantSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
    }

    /// <summary>
    /// Search principals by display label (case-insensitive contains over name/email).
    /// Optional <paramref name="type"/> narrows to <c>Person</c> or <c>Group</c>.
    /// Capped at 50 results.
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<PrincipalLookupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] string? type,
        CancellationToken ct)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<PrincipalDirectory>().Where(p => !p.IsDeleted);

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(p => p.Type == type);

        var results = await query.Take(50).ToListAsync(ct);

        // Filter by display label client-side — JSONB-path queries for nested
        // Person.Firstname comparisons get awkward; the in-process filter on a
        // 50-row cap is fine for autocomplete latency.
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            results = results
                .Where(p => p.GetDisplayLabel().Contains(needle, StringComparison.OrdinalIgnoreCase)
                         || (p.Email is not null && p.Email.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return Ok(results.Select(Map).ToList());
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PrincipalLookupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var principal = await session.LoadAsync<PrincipalDirectory>(id, ct);
        if (principal is null || principal.IsDeleted) return NotFound();
        return Ok(Map(principal));
    }

    private static PrincipalLookupDto Map(PrincipalDirectory p) => new()
    {
        Id = p.Id,
        Type = p.Type,
        DisplayLabel = p.GetDisplayLabel(),
        Email = p.Email,
        IsActive = p.IsActive,
        CanAuthenticate = p.CanAuthenticate,
        IsContainer = p.IsContainer,
    };
}

public record PrincipalLookupDto
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }
    public required string DisplayLabel { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public bool CanAuthenticate { get; init; }
    public bool IsContainer { get; init; }
}
