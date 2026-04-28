// Session self-service models — mirror DTOs in
// src/dotnet-next/Cocoar.Auth.Authentication/Sessions/SessionDtos.cs.

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
}
