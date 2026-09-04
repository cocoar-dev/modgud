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
#
# tini: a real PID 1. When dotnet itself is PID 1 the kernel never delivers
# the SIGABRT the runtime raises on an unhandled exception, so a crashed
# process keeps spinning at 100 % CPU and Docker still reports it "Up" - the
# restart policy never fires. tini forwards signals, reaps zombies and lets
# the container exit so `restart:` can do its job.
# curl: for the HEALTHCHECK below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 tini curl \
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

# Readiness as the container health: master DB reachable + OpenIddict cert
# ready. `docker ps` then shows the real state and `depends_on:
# condition: service_healthy` works for consumers. start-period covers the
# bounded Postgres wait at boot (DbSettings__StartupTimeoutSeconds, 90 s by
# default) plus schema apply. Override the URL when AppUrl is changed.
ENV HEALTHCHECK_URL=http://127.0.0.1:8081/health/ready
HEALTHCHECK --interval=15s --timeout=5s --start-period=120s --retries=3 \
    CMD curl -fsS "$HEALTHCHECK_URL" > /dev/null || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "Modgud.Api.dll"]
