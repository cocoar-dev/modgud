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

/** Human-readable resource labels. */
export const RESOURCE_LABELS: Record<string, string> = {
  app: 'App',
}
