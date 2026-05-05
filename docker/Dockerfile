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
# The website/ tree has a dedicated "in-app" config (config.in-app.ts)
# that sets `base: '/docs/'` and points outDir at `.vitepress/dist-in-app`.
# VitePress 1.6.x silently ignores `--config` on the build command, so
# we route around the bug by overwriting config.ts with the in-app
# variant before the build. The public marketing site is never built
# in the image, only the in-app variant that knows it lives under /docs.
FROM node:22-slim AS userdocs-build
ENV CI=true
RUN corepack enable && corepack prepare pnpm@latest --activate
WORKDIR /src
COPY website/ ./
RUN pnpm install --frozen-lockfile
RUN cp .vitepress/config.in-app.ts .vitepress/config.ts
RUN pnpm build

# ── Stage 4: Runtime ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Copy the published .NET app
COPY --from=backend-build /app ./

# Vue admin SPA → wwwroot/
COPY --from=frontend-build /src/dist ./wwwroot/

# VitePress build → wwwroot/docs/ (DocsExtensions middleware serves
# this under /docs/ — see Cocoar.Auth.Api/ExtensionMethods/DocsExtensions.cs).
# Stage 3 swaps config.in-app.ts into place so outDir is dist-in-app.
COPY --from=userdocs-build /src/.vitepress/dist-in-app ./wwwroot/docs/

EXPOSE 8081

ENV ASPNETCORE_ENVIRONMENT=Production
ENV AppUrl=http://0.0.0.0:8081
ENV CertPath=
ENV CertPassword=

ENTRYPOINT ["dotnet", "Cocoar.Auth.Api.dll"]
