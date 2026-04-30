export interface ExternalLinkDto {
  Id: string
  /** ShortGuid of the LoginProvider that minted this link. */
  LoginProviderId: string
  /** DisplayName of the LoginProvider — server-resolved at read time. */
  ProviderDisplayName: string
  Issuer: string
  Email?: string | null
  DisplayName?: string | null
  LinkedAt: string
  LastLoginAt: string

  /** When the last script run happened. Null if no login yet. */
  LastCapturedAt?: string | null
  /** Did the last user-update-script run cleanly? */
  LastScriptSucceeded: boolean
  /** Error message from the last script run, if it failed. */
  LastScriptError?: string | null
  /**
   * Raw object the user-update-script returned at the most recent login —
   * free-form JSON, shape decided by the script author. Debugging only.
   */
  LastScriptOutput?: Record<string, unknown> | null
  /**
   * Raw OIDC claims the IdP actually sent. Only present when the LoginProvider
   * has StoreRawClaims=true. Shape is a JSON object keyed by claim-type, with
   * scalar or array values — mirrors what flowed through the token.
   */
  LastRawClaims?: Record<string, unknown> | null
}
