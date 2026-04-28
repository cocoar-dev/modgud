export interface RoleDto {
  Id: string
  Name: string
  Description?: string
  ResourceType: string
  Permissions: string[]
}

/**
 * All available permission actions grouped by resource.
 * Must match backend ResourceRegistry.
 */
export const PERMISSION_RESOURCES: Record<string, string[]> = {
  app: ['admin'],
}

/** Human-readable resource labels (German). */
export const RESOURCE_LABELS: Record<string, string> = {
  app: 'App',
}

/**
 * Actions that write or mutate data on their resource. Used by the group editor
 * to warn when a write-bearing role is combined with no access script (= unrestricted
 * write access on the entire resource).
 */
export const WRITE_ACTIONS: Record<string, readonly string[]> = {}

export function roleHasWritePermission(role: Pick<RoleDto, 'ResourceType' | 'Permissions'>): boolean {
  const writeSet = WRITE_ACTIONS[role.ResourceType]
  if (!writeSet) return false
  return role.Permissions.some(p => writeSet.includes(p))
}
