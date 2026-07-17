# ── Stage 1: Build backend ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY src/dotnet/ ./
RUN dotnet restore Modgud.Api/Modgud.Api.csproj
RUN dotnet publish Modgud.Api/Modgud.Api.csproj -c Release -o /app --no-restore

# ── Stage 2: Build frontend (Vue admin SPA) ────────────────────────
FROM node:22-slim AS frontend-build
ENV CI=true
RUN corepack enable && corepack prepare pnpm@10.13.1 --activate
WORKDIR /src
COPY src/frontend-vue/ ./
RUN pnpm install --frozen-lockfile
RUN pnpm build

# ── Stage 3: Build docs (VitePress, in-app variant) ────────────────
# The docs/ tree has a dedicated "in-app" config (config.in-app.ts)
# that sets `base: '/docs/'` and points outDir at `.vitepress/dist-in-app`.
# Both variants build the same content — the in-app version just
# carries a different `base` URL and a slimmed-down nav.
FROM node:22-slim AS userdocs-build
ENV CI=true
RUN corepack enable && corepack prepare pnpm@10.13.1 --activate
WORKDIR /src
COPY docs/ ./
RUN pnpm install --frozen-lockfile
RUN pnpm build:in-app

# ── Stage 4: Runtime ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Npgsql probes for GSSAPI/Kerberos when opening a connection; without the
# library present it logs a noisy "Cannot load libgssapi_krb5.so.2" error
# before falling back to password auth. Install it so the boot / recovery-CLI
# logs stay clean. (Kept above the app COPYs so the apt layer caches.)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Copy the published .NET app
COPY --from=backend-build /app ./

# Vue admin SPA → wwwroot/
COPY --from=frontend-build /src/dist ./wwwroot/

# VitePress build → wwwroot/docs/ (DocsExtensions middleware serves
# this under /docs/ — see Modgud.Api/ExtensionMethods/DocsExtensions.cs).
# Stage 3 swaps config.in-app.ts into place so outDir is dist-in-app.
COPY --from=userdocs-build /src/.vitepress/dist-in-app ./wwwroot/docs/

EXPOSE 8081

ENV ASPNETCORE_ENVIRONMENT=Production
ENV AppUrl=http://0.0.0.0:8081
ENV CertPath=
ENV CertPassword=

# Run as the non-root `app` user shipped in the aspnet base image (defense in
# depth, audit L9). On first Production boot CertificateBootstrap auto-generates
# the signing/encryption PFX under /app/data/keys, so that tree must be
# app-writable; everything else the app persists lives in Postgres (Marten),
# including the DataProtection key ring. Port 8081 is >1024, so an unprivileged
# user can bind it. Operators mounting a volume at /app/data/keys must ensure it
# is writable by uid `app`.
RUN mkdir -p /app/data/keys && chown -R app:app /app/data
USER app

ENTRYPOINT ["dotnet", "Modgud.Api.dll"]
