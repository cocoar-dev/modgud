// Session self-service models — mirror DTOs in
// src/dotnet-next/Modgud.Authentication/Sessions/SessionDtos.cs.

export interface SessionDto {
  Id: string
  IpAddress?: string | null
  Browser?: string | null
  BrowserVersion?: string | null
  OperatingSystem?: string | null
  OsVersion?: string | null
  DeviceType?: string | null
  CreatedAt: string
  LastActiveAt: string
  IsCurrent: boolean
}

export interface SessionListDto {
  Sessions: SessionDto[]
  ClientSessions: ClientSessionDto[]
}

export interface ClientSessionDto {
  Id: string
  ClientId: string
  ClientDisplayName?: string | null
  IpAddress?: string | null
  Browser?: string | null
  BrowserVersion?: string | null
  OperatingSystem?: string | null
  OsVersion?: string | null
  DeviceType?: string | null
  CreatedAt: string
  LastActiveAt: string
  ExpiresAt: string
  AbsoluteExpiresAt: string
}
