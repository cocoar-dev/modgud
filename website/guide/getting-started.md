# Getting Started

## Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL via Docker Compose or Testcontainers)
- Node.js 20+ and pnpm

## Running the Backend

```bash
# Start PostgreSQL
docker compose up -d

# Build and run
cd src/dotnet
dotnet build
dotnet run --project Cocoar.Auth.Api
```

The API starts on `http://localhost:80`.

## Running the Frontend

```bash
cd src/frontend-vue/apps/frontend
pnpm install
pnpm dev
```

The Vue dev server starts on `http://localhost:4200` with proxy rules forwarding `/{realm}/api` requests to the backend.

## First-Time Setup

1. Navigate to `http://localhost:4200/`
2. You'll be redirected to the **Initial Setup** page
3. Create the first admin account (username, password)
4. You're auto-logged-in as admin

## Running Tests

```bash
cd src/dotnet
dotnet test
```

All tests are integration tests using Testcontainers (PostgreSQL in Docker). ~250 tests covering all features.
