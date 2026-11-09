// F7-01 / F7-02 — an App's settings override is sparse: a null field inherits the realm
// value. The editor fills its form with concrete defaults (null → false, null → []), so a
// save that touched nothing materialized those defaults and changed the effective state
// (an inherited RequireAdminApproval=true became false, LoginProviderIds null — "all
// enabled providers" — became [] — "none"). For a section the App already had, a field the
// admin did not change is sent back exactly as it was loaded.

function sameValue(a: unknown, b: unknown): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null)
}

/**
 * The value to send for one field of an override section.
 * @param originalSection the section as loaded (null/undefined when the App had none)
 * @param field the field name in that section
 * @param loadedFormValue the form value right after loading
 * @param currentFormValue the form value now
 * @param builtValue what the form would send (used when the field changed or the section is new)
 */
export function keepUnchanged(
  originalSection: object | null | undefined,
  field: string,
  loadedFormValue: unknown,
  currentFormValue: unknown,
  builtValue: unknown,
): unknown {
  if (!originalSection) return builtValue
  if (!sameValue(loadedFormValue, currentFormValue)) return builtValue
  return (originalSection as Record<string, unknown>)[field] ?? null
}
