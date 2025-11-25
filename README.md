# Cocoar.Auth

Authentication and authorization services for COCOAR applications.

[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

---

## Overview

Cocoar.Auth provides a comprehensive authentication and authorization solution built with:
- **Backend**: ASP.NET Core 9.0 API
- **Frontend**: Angular 20 with Nx
- **Standards**: OAuth 2.0 / OpenID Connect
- **Database**: PostgreSQL
- **Deployment**: Docker containers

## Architecture

```
cocoar.auth/
├── src/
│   ├── dotnet/      # ASP.NET Core API
│   └── frontend/     # Angular UI
├── docker/           # Docker deployment configurations
└── docs/             # Documentation
```

## Features

- 🔐 OAuth 2.0 / OpenID Connect support
- 👤 User management
- 🔑 API key management
- 🎫 Token-based authentication
- 🛡️ Role-based access control (RBAC)
- 🔒 Multi-factor authentication (MFA)
- 📱 Social login providers

## Development

**Prerequisites:**
- .NET 9.0 SDK
- Node.js 20+
- Docker Desktop
- PostgreSQL (via Docker)

**Getting Started:**

```powershell
# Clone repository
git clone https://github.com/cocoar-dev/cocoar.auth.git
cd cocoar.auth

# Backend
cd src/dotnet
dotnet restore
dotnet build
dotnet run

# Frontend
cd src/frontend
npm install
npm start
```

## Project Status

🚧 **In Development** - Early stage

## License

Copyright © 2025 COCOAR e.U.

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.

## Contact

COCOAR e.U.  
Email: bwi@cocoar.dev  
Web: https://cocoar.dev

---

**Built with ❤️ by COCOAR**
