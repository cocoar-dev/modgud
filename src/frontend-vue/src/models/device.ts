// Device Authorization Grant (RFC 8628) verification — SPA models.
// Mirrors DeviceVerificationInfo / DeviceScopeInfo in
// src/dotnet/Modgud.Api/Features/Auth/OAuth/DeviceVerificationEndpoints.cs.

export interface DeviceScopeInfo {
  Name: string
  DisplayName: string
  Description?: string | null
}

export type DeviceVerificationStatus = 'needs_code' | 'ready' | 'invalid_code'

export interface DeviceVerificationInfo {
  Ticket: string
  Status: DeviceVerificationStatus
  /** Normalized user code, echoed back so the SPA can submit it to
   * POST /connect/verify for the decision. Only set when Status === 'ready'. */
  UserCode?: string | null
  ClientName?: string | null
  Scopes: DeviceScopeInfo[]
}
