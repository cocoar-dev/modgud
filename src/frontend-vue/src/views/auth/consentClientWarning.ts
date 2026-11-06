/** The consent model fields that decide whether the user must be warned about the client. */
export interface ConsentWarningSource {
  ClientIdHostname?: string | null
  IsDynamicallyRegistered?: boolean | null
}

/** Which warning the consent screen must show, if any. */
export type ConsentClientWarning =
  | { kind: 'cimd', host: string }
  | { kind: 'dcr' }
  | null

/**
 * B10-PA03 — the unverified-client warning is decided and rendered by the host, never by
 * an editable page document: a URL-identified client (CIMD) is named by its domain, a
 * self-registered one (DCR) is flagged as unverified. The fixed and the custom consent
 * screen both show exactly this.
 */
export function consentClientWarning(model: ConsentWarningSource | null | undefined): ConsentClientWarning {
  if (!model) return null
  if (model.ClientIdHostname) return { kind: 'cimd', host: model.ClientIdHostname }
  if (model.IsDynamicallyRegistered) return { kind: 'dcr' }
  return null
}
