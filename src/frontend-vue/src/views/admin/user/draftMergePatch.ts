// AID-06 — a staged user entity is a JSON merge patch: an ABSENT field keeps the live
// value, an explicit null (or empty string) is a staged clear. Shared by the details form
// overlay and the list preview so both read a partial patch the same way.

/** The staged text of a merge-patch field, or the live value when the patch leaves it out. */
export function stagedText(entity: Record<string, unknown>, key: string, live: string): string
export function stagedText(entity: Record<string, unknown>, key: string, live: string | undefined): string | undefined
export function stagedText(entity: Record<string, unknown>, key: string, live: string | undefined): string | undefined {
  if (!(key in entity)) return live
  const value = entity[key]
  return typeof value === 'string' ? value : ''
}

/** The staged boolean of a merge-patch field, or the live value when the patch leaves it out. */
export function stagedFlag(entity: Record<string, unknown>, key: string, live: boolean): boolean {
  const value = entity[key]
  return typeof value === 'boolean' ? value : live
}
