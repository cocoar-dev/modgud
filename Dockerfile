# Local development multi-stage build.
# For CI/CD, see docker/Dockerfile (uses pre-built artifacts).
#
# Usage: docker build -t cocoar-auth .
#
# ══════════════════════════════════════════════════════════════════
# Stage 1: Build Vue Frontend
# ══════════════════════════════════════════════════════════════════
FROM node:22-alpine AS frontend-build

RUN corepack enable && corepack prepare pnpm@10 --activate

WORKDIR /app/frontend
COPY src/frontend-vue/package.json src/frontend-vue/pnpm-workspace.yaml src/frontend-vue/pnpm-lock.yaml src/frontend-vue/turbo.json ./
COPY src/frontend-vue/apps/frontend/package.json apps/frontend/package.json

RUN pnpm install --frozen-lockfile

COPY src/frontend-vue/apps/frontend apps/frontend

RUN pnpm -C apps/frontend build

# ══════════════════════════════════════════════════════════════════
# Stage 2: Build .NET Backend
# ══════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS backend-build

WORKDIR /app
COPY src/dotnet/Directory.Build.props src/dotnet/Directory.Packages.props src/dotnet/Cocoar.Auth.sln ./
COPY src/dotnet/Cocoar.Primitives/Cocoar.Primitives.csproj Cocoar.Primitives/
COPY src/dotnet/Cocoar.Auth.Domain/Cocoar.Auth.Domain.csproj Cocoar.Auth.Domain/
COPY src/dotnet/Cocoar.Auth.Application/Cocoar.Auth.Application.csproj Cocoar.Auth.Application/
COPY src/dotnet/Cocoar.Auth.Infrastructure/Cocoar.Auth.Infrastructure.csproj Cocoar.Auth.Infrastructure/
COPY src/dotnet/Cocoar.Auth.Api/Cocoar.Auth.Api.csproj Cocoar.Auth.Api/

RUN dotnet restore Cocoar.Auth.Api/Cocoar.Auth.Api.csproj

COPY src/dotnet/Cocoar.Primitives Cocoar.Primitives/
COPY src/dotnet/Cocoar.Auth.Domain Cocoar.Auth.Domain/
COPY src/dotnet/Cocoar.Auth.Application Cocoar.Auth.Application/
COPY src/dotnet/Cocoar.Auth.Infrastructure Cocoar.Auth.Infrastructure/
COPY src/dotnet/Cocoar.Auth.Api Cocoar.Auth.Api/

RUN dotnet publish Cocoar.Auth.Api/Cocoar.Auth.Api.csproj -c Release -o /publish --no-restore

# ══════════════════════════════════════════════════════════════════
# Stage 3: Runtime Image
# ══════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime

WORKDIR /app

# Copy published .NET app
COPY --from=backend-build /publish .

# Copy built Vue SPA into wwwroot
COPY --from=frontend-build /app/frontend/apps/frontend/dist wwwroot/

# Health check
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

EXPOSE 80

ENTRYPOINT ["dotnet", "Cocoar.Auth.Api.dll"]
