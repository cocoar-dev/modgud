// Per-realm asset library. Mirrors
// src/dotnet/Cocoar.Auth.Application/DTOs/Assets/AssetDtos.cs.

export interface AssetDto {
  Id: string
  FileName: string
  ContentType: string
  SizeBytes: number
  Sha256: string
  UploadedAt: string
  UploadedByUsername: string | null
  /** Public URL — `<img :src="asset.Url">` works as-is, no proxy needed. */
  Url: string
}

/** 409 body when DELETE refuses because the asset is still referenced
 * (e.g. set as the realm logo). */
export interface AssetInUseDto {
  Id: string
  References: string[]
}

export const ALLOWED_ASSET_MIME_TYPES: readonly string[] = [
  'image/png',
  'image/jpeg',
  'image/gif',
  'image/webp',
  'image/svg+xml',
  'image/x-icon',
  'image/vnd.microsoft.icon',
]

export const MAX_ASSET_SIZE_BYTES = 2 * 1024 * 1024 // 2 MiB
