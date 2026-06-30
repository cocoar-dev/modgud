/**
 * Generic "Clone / Copy" affordance for admin entities.
 *
 * Slugs, client_ids and audiences are immutable by design — so the way to
 * "rename" an entity is to clone it under a new identity and re-point the
 * references. This composable is the plumbing for that: a List stages a
 * prefill (the source entity with its immutable identity blanked and its
 * server-issued secrets dropped) and navigates to the entity's Create modal;
 * the Create modal consumes the prefill on mount and maps it into its form.
 *
 * The fragment-routed modals only carry an `id` slot in the URL (`create`),
 * so the prefill rides out-of-band through this module-level stash instead of
 * the route. `consume()` is single-use (it clears on read) and keyed by entity
 * so a stale stash can never bleed into a different entity's Create modal.
 */
import { ref } from 'vue'

interface CloneStash {
  entity: string
  prefill: Record<string, unknown>
}

// Module-level singleton: one pending clone at a time. Staging overwrites;
// consuming clears. A normal (non-clone) Create simply finds nothing.
const pending = ref<CloneStash | null>(null)

export function useClone() {
  /** Stash a prefill for the given entity, to be picked up by its Create modal. */
  function stage(entity: string, prefill: Record<string, unknown>): void {
    pending.value = { entity, prefill }
  }

  /**
   * Pop the staged prefill if it belongs to `entity`, else return null. Reads
   * are single-use — the stash is cleared so a re-opened blank Create starts
   * empty.
   */
  function consume<T = Record<string, unknown>>(entity: string): T | null {
    const p = pending.value
    if (!p || p.entity !== entity) return null
    pending.value = null
    return p.prefill as T
  }

  /** Drop any pending stash (e.g. on an aborted flow). */
  function clear(): void {
    pending.value = null
  }

  return { stage, consume, clear }
}

/**
 * Per-entity clone rule. Everything not named here is copied 1:1 from the
 * source DTO; `blank` fields are reset to '' (the immutable identity the admin
 * must re-enter), `drop` fields are removed entirely (secrets, server-issued
 * ids), and `reshape` runs last for entity-specific shaping.
 */
export interface CloneDescriptor {
  /** Identity fields reset to '' — the Create form re-validates them. */
  blank?: string[]
  /** Fields removed entirely (secrets, ids belonging to the source's streams). */
  drop?: string[]
  /** Final entity-specific reshape. */
  reshape?: (clone: Record<string, unknown>) => Record<string, unknown>
}

/**
 * Build a clone prefill from a full source DTO. The result is a detached deep
 * copy shaped exactly like the source DTO (so the Create modal's existing
 * `fromDto` can consume it), with identity blanked and secrets dropped.
 */
export function buildClonePrefill<T extends object>(
  source: T,
  descriptor: CloneDescriptor,
): Record<string, unknown> {
  const clone = structuredClone(source) as Record<string, unknown>
  for (const key of descriptor.blank ?? []) clone[key] = ''
  for (const key of descriptor.drop ?? []) delete clone[key]
  return descriptor.reshape ? descriptor.reshape(clone) : clone
}

// ── Per-entity descriptors ──────────────────────────────────────────────────
// The `entity` string is an internal match key shared between a List's stage()
// and its Create modal's consume() — it need only be stable, not user-facing.

export const APP_CLONE = {
  entity: 'app',
  descriptor: {
    blank: ['Slug'],
    drop: ['Id', 'IsSystem'],
    reshape: (c) => {
      // Catalog entries carry server-issued ids that belong to the SOURCE
      // app's event streams — null them so the clone mints fresh ids under
      // its own streams (role-grants / RS-subsets on the source are untouched).
      if (Array.isArray(c.Permissions)) {
        c.Permissions = (c.Permissions as Record<string, unknown>[]).map((p) => ({
          ...p,
          Id: null,
        }))
      }
      // Origin (subdomain) is globally unique — the clone can't claim the
      // source's. Drop just that override; the rest of the ADR-0011 settings
      // clone 1:1.
      if (c.Settings && typeof c.Settings === 'object') {
        c.Settings = { ...(c.Settings as Record<string, unknown>), Origin: null }
      }
      return c
    },
  } satisfies CloneDescriptor,
} as const

export const CLIENT_CLONE = {
  entity: 'oauth-client',
  descriptor: {
    blank: ['ClientId'],
    // Secret is hashed at rest (unclonable; create mints a fresh one). DCR
    // audit fields + SA-linkage belong to the original registration only.
    drop: [
      'Id',
      'ClientSecret',
      'IsDynamicallyRegistered',
      'DcrRegisteredAt',
      'DcrRegisteredFromIp',
      'DcrLastUsedAt',
      'LinkedServiceAccountId',
    ],
  } satisfies CloneDescriptor,
} as const

export const SCOPE_CLONE = {
  entity: 'oauth-scope',
  descriptor: {
    blank: ['Name'],
    drop: ['Id', 'IsStandard'],
  } satisfies CloneDescriptor,
} as const

export const API_CLONE = {
  entity: 'oauth-api',
  descriptor: {
    blank: ['Name'],
    // Secrets are server-issued; a clone starts with none.
    drop: ['Id', 'Secrets'],
  } satisfies CloneDescriptor,
} as const

export const ROLE_CLONE = {
  entity: 'role',
  descriptor: {
    blank: ['Name'],
    drop: ['Id'],
  } satisfies CloneDescriptor,
} as const

export const GROUP_CLONE = {
  entity: 'group',
  descriptor: {
    blank: ['Name'],
    drop: ['Id'],
  } satisfies CloneDescriptor,
} as const
