/**
 * R1 (post-merge review) — a settings document guards a later save with the version the view
 * loaded (F7-05). Some writes of the same view reach that document through other endpoints (the
 * page selection of the Pages tab, a lazy slot migration on listing), so the view's own write
 * turned its next save into a 409. Around such a write the view adopts the document's new
 * version, but only when nobody else had changed the document before it: a change made by
 * someone else still surfaces as the conflict.
 */
export async function adoptOwnVersion<T>(
  held: string | null | undefined,
  fetchVersion: () => Promise<string | null | undefined>,
  write: () => Promise<T>,
  adopt: (version: string | null) => void,
): Promise<T> {
  // No stored document yet: the save carries no version to keep.
  if (held == null) return write()
  const before = await fetchVersion()
  const result = await write()
  if (before === held) adopt((await fetchVersion()) ?? null)
  return result
}
