// Mirrors the server-side ticket variant of the OAuth consent flow.
// See src/dotnet/Cocoar.Auth.Api/Features/Auth/OAuth/ConsentEndpoints.cs
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
  /** True for clients minted via RFC 7591 Dynamic Client Registration.
   * Drives the [unverified] marker + warning text on the consent UI. */
  IsDynamicallyRegistered: boolean
}

export interface ConsentDecision {
  Ticket: string
  Approved: boolean
  ApprovedScopes: string[]
}

export interface ConsentResult {
  /** Non-SPA URL. Approve → /connect/authorize?<original-query>;
   * Deny → /consent/denied?error=… (an IdP-side error landing page).
   * Use window.location.assign — Vue Router cannot navigate to either. */
  RedirectUrl: string
}
