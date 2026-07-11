// Mirrors the server-side ticket variant of the OAuth consent flow.
// See src/dotnet/Modgud.Api/Features/Auth/OAuth/ConsentEndpoints.cs
// for the full contract; the SPA receives a fully-resolved consent
// model from /connect/consent?ticket=… and posts a decision back to
// the same path. The redirect target is rebuilt server-side from the
// locked-in query string, so the SPA never sees the OAuth URL.

export interface ConsentScopeInfo {
  Name: string
  DisplayName: string
  Description?: string | null
  /** OIDC core scope (today: openid). Required scopes are always
   * approved and cannot be unchecked. */
  Required: boolean
}

export interface ConsentModel {
  Ticket: string
  ClientId: string
  ClientName: string
  RequestedScopes: ConsentScopeInfo[]
  ExpiresAt: string
  /** True for clients minted via RFC 7591 DCR or resolved via a CIMD
   * client_id URL. Drives the [unverified] marker + warning
   * text on the consent UI. */
  IsDynamicallyRegistered: boolean
  /** For a CIMD client, the hostname of the client_id URL (e.g.
   * "claude.ai") — the domain that owns the metadata document. Null for
   * DCR / admin-registered clients. Shown prominently as a phishing
   * mitigation: verify the real domain, not just the display name. */
  ClientIdHostname?: string | null
}

export interface ConsentDecision {
  Ticket: string
  Approved: boolean
  ApprovedScopes: string[]
}

export interface ConsentResult {
  /** Non-SPA, same-origin /connect/authorize URL. Approve → re-enters with
   * the original query to complete the grant; Deny → re-enters with a
   * deny marker (?deny_ticket=…) so OpenIddict emits the RFC 6749
   * error=access_denied to the client honoring its response_mode + iss.
   * Use window.location.assign — Vue Router cannot navigate to it. */
  RedirectUrl: string
  /** True on a deny that hands control back to the client via the authorize
   * re-entry (vs. the defensive inline-denied fallback). Both cases
   * full-page-navigate to a same-origin RedirectUrl. */
  ReturnsToClient?: boolean
}
