# ── Stage 1: Build backend ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY src/dotnet/ ./
RUN dotnet restore Cocoar.Auth.Api/Cocoar.Auth.Api.csproj
RUN dotnet publish Cocoar.Auth.Api/Cocoar.Auth.Api.csproj -c Release -o /app --no-restore

# ── Stage 2: Build frontend (Vue admin SPA) ────────────────────────
FROM node:22-slim AS frontend-build
ENV CI=true
RUN corepack enable && corepack prepare pnpm@latest --activate
WORKDIR /src
COPY src/frontend-vue/ ./
RUN pnpm install --frozen-lockfile
RUN pnpm build

# ── Stage 3: Build docs (VitePress) ────────────────────────────────
# The website/ tree has a separate "in-app" config (config.in-app.ts)
# but VitePress 1.6.x silently ignores `--config` on the build command,
# so for now we ship the public build into the container's /docs/.
# Trade-off: the in-container help has the public marketing nav. Tracked
# as a P1 follow-up — fix the in-app config wiring once VitePress
# upstream picks up the flag (or switch to a custom build script).
FROM node:22-slim AS userdocs-build
ENV CI=true
RUN corepack enable && corepack prepare pnpm@latest --activate
WORKDIR /src
COPY website/ ./
RUN pnpm install --frozen-lockfile
RUN pnpm build

# ── Stage 4: Runtime ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Copy the published .NET app
COPY --from=backend-build /app ./

# Vue admin SPA → wwwroot/
COPY --from=frontend-build /src/dist ./wwwroot/

# VitePress build → wwwroot/docs/ (DocsExtensions middleware serves
# this under /docs/ — see Cocoar.Auth.Api/ExtensionMethods/DocsExtensions.cs)
COPY --from=userdocs-build /src/.vitepress/dist ./wwwroot/docs/

EXPOSE 8081

ENV ASPNETCORE_ENVIRONMENT=Production
ENV AppUrl=http://0.0.0.0:8081
ENV CertPath=
ENV CertPassword=

ENTRYPOINT ["dotnet", "Cocoar.Auth.Api.dll"]
