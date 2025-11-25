# Frontend

🚧 **Planned** - Angular application for Cocoar.Auth

## Current Status

The frontend is planned for a future phase. Currently, the backend API can be tested using:
- Integration tests (`dotnet test`)
- API clients (Postman, curl, etc.)
- Swagger UI (when running the API)

## Planned Structure

```
frontend/
├── apps/
│   └── auth-app/           # Main authentication app
├── libs/
│   ├── ui/                 # Shared UI components
│   ├── auth/               # Authentication logic
│   └── shared/             # Shared utilities
└── package.json
```

## Planned Technology Stack

- Angular 20+
- Nx (monorepo management)
- TypeScript
- TailwindCSS
- RxJS

## Backend API (Available Now)

The backend API is fully functional with 63 passing tests:

```powershell
# Run the API
cd src/dotnet
dotnet run --project Cocoar.Auth.Api

# API will be available at:
# http://localhost:5000/api
```

See [API Reference](../../docs/api-reference.md) for endpoint documentation.
