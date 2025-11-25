# Contributing to Cocoar.Auth

Thank you for your interest in contributing to Cocoar.Auth!

## Getting Started

1. Fork the repository
2. Clone your fork: `git clone https://github.com/YOUR_USERNAME/cocoar.auth.git`
3. Create a feature branch: `git checkout -b feature/my-new-feature`
4. Make your changes
5. Run tests to ensure everything works
6. Commit your changes: `git commit -am 'Add some feature'`
7. Push to the branch: `git push origin feature/my-new-feature`
8. Create a Pull Request

## Development Setup

**Prerequisites:**
- .NET 9.0 SDK
- Node.js 20+
- Docker Desktop
- Git

**Backend Setup:**
```powershell
cd src/dotnet
dotnet restore
dotnet build
dotnet test
```

**Frontend Setup:**
```powershell
cd src/frontend
npm install
npm run build
npm test
```

## Code Standards

- Follow the existing code style
- Write meaningful commit messages
- Add tests for new features
- Update documentation as needed
- Ensure all tests pass before submitting PR

## Pull Request Process

1. Update the README.md with details of changes if applicable
2. Update documentation for any new features
3. The PR will be merged once you have approval from maintainers

## Code of Conduct

Please read and follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## Questions?

Feel free to reach out to bwi@cocoar.dev with any questions.

## License

By contributing, you agree that your contributions will be licensed under the Apache License 2.0.
