using Cocoar.Auth.Domain.Entities;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Resolves the effective roles for a user — combining direct role assignments
/// with roles inherited through group membership (including nested groups).
/// </summary>
public interface IEffectiveRolesService
{
	Task<IReadOnlyList<ApplicationRole>> GetEffectiveRolesAsync(Guid userId, CancellationToken ct = default);
}
