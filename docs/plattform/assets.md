# Customization — Asset Library

Per-realm image library for branding (and, later, page-builder schemas). Upload once, reference by ID from any branding field. Each realm's library is fully isolated — assets live in the tenant DB and can never be referenced from another realm.

::: warning Trust boundary: SVG and image MIME
The asset library accepts SVG, but every upload is sanitised on the server before the bytes touch storage. The MIME type is sniffed from the leading magic bytes — **the client's `Content-Type` header is never trusted**. Anything that doesn't match the allowlist is rejected outright.
:::

Permissions: `asset:read` to list / view, `asset:write` to upload / delete. The `realm:admin` bypass grants both.

## Limits and allowlist

| Limit | Value | Notes |
| --- | --- | --- |
| Max file size | **2 MiB** | Hard cap per upload. Hits return HTTP 400 with the explicit limit. |
| Allowed MIME types | PNG, JPEG, GIF, WebP, SVG, ICO | Sniffed from magic bytes. Anything else → reject. |
| Filename | Free text | Used only for display; the storage key is the asset id (a UUIDv7). |
| Per-realm count | unlimited (no quota in v1) | Storage-quota enforcement is a future feature — see roadmap. |

::: tip Why magic-byte sniffing
A malicious uploader could send `image/png` as their `Content-Type` while the bytes are actually an executable. The MIME-type allowlist alone doesn't help — only the magic-byte sniff does. The allowlist is a separate guard on top of the sniff, not a replacement for it.
:::

## SVG sanitisation

SVG goes through a dedicated cleanup pass before it lands in storage. The sanitiser:

1. Parses the SVG with `DtdProcessing.Ignore` + `XmlResolver = null` — no external entity resolution, no DTD-based attacks (XXE). If parsing fails, the upload is rejected (`Asset.SvgNotWellFormed`); we never persist SVG we can't fully parse.
2. **Removes `<script>` and `<foreignObject>` nodes entirely.**
3. **Strips every `on*=` attribute** (event handlers — `onclick`, `onload`, …).
4. **Strips `href` / `xlink:href` attributes** whose value begins with `javascript:` or `data:text/html`.
5. Re-serialises the cleaned tree and stores that.

The on-disk bytes are the sanitised version. The browser only ever sees what the sanitiser kept.

## Upload flow

1. Open **Administration → Customization → Asset Library**.
2. Drag a file onto the upload zone, or click **Upload** and pick from the file dialog.
3. The grid refreshes with the new asset — preview thumbnail, filename, content type, size, upload date, uploader.
4. Reference the asset from [Branding](./branding) by opening the Logo or Favicon picker.

## Public read endpoint

Assets are served on the **anonymous** path `/api/assets/{id}` (note the `/api/` prefix — picked deliberately to avoid colliding with the SPA's `/assets/` build output in production and Vite's `/assets/` dev path).

| Property | Value |
| --- | --- |
| Path | `GET /api/assets/{shortGuid}` |
| Auth | None — assets are designed to be embedded as branding on the public login page |
| Cache | `ETag: "<sha256>"` + conditional `304 Not Modified` |
| Content-Type | The sanitised value sniffed at upload time, not whatever the client originally claimed |
| Body | The sanitised bytes |

Anonymous-readable is intentional — branding has to render before the user authenticates. There are no secrets in the asset library by design; if you wouldn't put an image on your public marketing site, don't put it in the asset library.

## Delete protection

The asset-library endpoint refuses to delete an asset that is **currently referenced** by the realm's branding. Hitting `DELETE /api/admin/assets/{id}` on a referenced asset returns HTTP 409 with the referencing field name in the error body. The SPA surfaces this as a clear error.

To delete a referenced asset:

1. Open [Branding](./branding).
2. Clear the field that references the asset (Logo or Favicon).
3. **Save** the branding form.
4. Return to **Asset Library** and delete the asset.

::: info Why not cascade-clear
We don't auto-clear branding when an asset is deleted — that would silently revert your branding without confirmation. Explicit two-step is the safer default.
:::

## What's stored where

| Data | Location |
| --- | --- |
| Asset metadata (id, filename, content type, size, sha256, uploadedAt, uploadedBy) | tenant DB, `mt_doc_asset` |
| Asset binary | tenant DB, `mt_doc_asset.Data` column (BYTEA) |

Each realm's assets live in its own tenant DB. There is no shared CDN, no cross-realm reference, and no way for one realm to enumerate another realm's library — the standard Marten tenant scope handles that automatically.

## Future

- **Storage quota / per-realm cap** — not enforced in v1. Per-realm provisioning quotas are tracked as a planned item on the [roadmap](../roadmap).
- **Object-storage backend** — assets live in BYTEA today. For deployments with very many or very large assets, a backend that pages out to S3 / Hetzner Storage Box is a possible future tier; the public read endpoint stays the same.
- **Versioning / replacement-without-id-change** — the current model treats each upload as immutable; replacing a logo means uploading a new asset and switching the branding reference. That's by design (cache-buster comes for free via the new id) but limits "edit the image in place" workflows.
