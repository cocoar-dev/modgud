// Device Authorization Grant (RFC 8628) verification — SPA models.
// Mirrors DeviceVerificationInfo / DeviceScopeInfo in
// src/dotnet/Modgud.Api/Features/Auth/OAuth/DeviceVerificationEndpoints.cs.

export interface DeviceScopeInfo {
  Name: string
  DisplayName: string
  Description?: string | null
}

export type DeviceVerificationStatus = 'needs_code' | 'ready' | 'invalid_code'

/** MG-FT-04 — what the approving admin must see before registering a device
 * as a position terminal (§11.4). */
export interface TerminalConsentInfo {
  PositionName: string
  TerminalName: string
  Location?: string | null
  ClientId: string
  /** Null/absent when the device request carried no DPoP proof — the approval
   * will be refused server-side in that case. */
  DpopFingerprint?: string | null
}

export interface DeviceVerificationInfo {
  Ticket: string
  Status: DeviceVerificationStatus
  /** Normalized user code, echoed back so the SPA can submit it to
   * POST /connect/verify for the decision. Only set when Status === 'ready'. */
  UserCode?: string | null
  ClientName?: string | null
  Scopes: DeviceScopeInfo[]
  /** 'user' = ordinary person device flow; 'terminal' = terminal enrollment
   * (MG-FT-04) — render the terminal consent instead of the scope consent. */
  Kind?: 'user' | 'terminal'
  /** Set when Kind === 'terminal'. */
  Terminal?: TerminalConsentInfo | null
}
