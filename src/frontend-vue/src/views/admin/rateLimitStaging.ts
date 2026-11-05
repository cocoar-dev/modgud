// F7-06 — staging rate-limit changes onto a draft. The rate-limit patch is a nested merge
// patch (policy → dimension → rule; null on a policy drops all its overrides, null on a
// dimension returns it to the baseline). Merging two staged patches one level deep made the
// second staged Policies map replace the first; this merges per policy and per dimension.

import { RATE_LIMIT_DIMENSIONS, type UpdateAuthRateLimitsDto, type UpdatePolicyLimitsDto } from '@/models/realmSettings'

function mergePolicy(prev: UpdatePolicyLimitsDto | null | undefined, next: UpdatePolicyLimitsDto | null): UpdatePolicyLimitsDto | null {
  if (next === null) return null
  // A previously staged null cleared every dimension: keep that for the dimensions the
  // new patch does not set.
  const base: UpdatePolicyLimitsDto = prev === null
    ? Object.fromEntries(RATE_LIMIT_DIMENSIONS.map((d) => [d.key, null]))
    : { ...(prev ?? {}) }
  return { ...base, ...next }
}

/** Combines a staged rate-limit patch with a newer one (absent = unchanged). */
export function mergeRateLimitPatch(
  prev: UpdateAuthRateLimitsDto | null | undefined,
  next: UpdateAuthRateLimitsDto,
): UpdateAuthRateLimitsDto {
  const out: UpdateAuthRateLimitsDto = { ...(prev ?? {}) }
  if (next.Policies !== undefined) {
    const policies = { ...(prev?.Policies ?? {}) }
    for (const [name, patch] of Object.entries(next.Policies))
      policies[name] = mergePolicy(prev?.Policies?.[name], patch)
    out.Policies = policies
  }
  if (next.SourceAllowlist !== undefined) out.SourceAllowlist = next.SourceAllowlist
  if (next.Mode !== undefined) out.Mode = next.Mode
  if (next.ClearLegacy !== undefined) out.ClearLegacy = next.ClearLegacy
  return out
}
