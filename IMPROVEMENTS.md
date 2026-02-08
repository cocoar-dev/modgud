# Cocoar.Auth - Improvement Tracker

> **Created:** 2026-02-07
> **Last Updated:** 2026-02-08
> **Purpose:** Track identified improvements, gaps, and their implementation status.

---

## Overview

This document tracks all identified improvements from the repository analysis performed on 2026-02-07. Items are organized by priority and grouped by area. Each item is marked with its current status.

**Legend:**
- [ ] Not started
- [x] Completed
- [~] In progress

---

## P0 — Before Production

These items are critical for a production-ready identity provider.

### P0.1 — Backend Hardening

| # | Item | Area | Status |
|---|------|------|--------|
| 1 | Add health check endpoint (`/health` with DB check) | API | [x] |
| 2 | Add API rate limiting (especially auth endpoints: login, register, forgot-password) | API | [x] |
| 3 | Add structured logging (Serilog with structured output) | Cross-cutting | [x] |
| 4 | Add security headers middleware (CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy) | API | [x] |
| 5 | Fix TODO in AuthController — session ID retrieval from cookie/claims | API | [x] |

### P0.2 — Containerization

| # | Item | Area | Status |
|---|------|------|--------|
| 6 | Create Dockerfile for API (multi-stage, non-root, health check) | DevOps | [ ] |
| 7 | Create Dockerfile for Angular frontend (Node build + Nginx serve) | DevOps | [ ] |
| 8 | Create docker-compose.yml for local development (API + frontend + PostgreSQL + MailHog) | DevOps | [ ] |
| 9 | Create .dockerignore files | DevOps | [ ] |

### P0.3 — Critical Test Gaps

| # | Item | Area | Status |
|---|------|------|--------|
| 10 | Add integration tests for OAuth authorization flows (`/connect/authorize`, `/connect/token`) | Tests | [x] |
| 11 | Add integration tests for token refresh flow | Tests | [x] |
| 12 | Add integration tests for client credentials flow | Tests | [x] |

### P0.4 — Frontend Critical Fixes

| # | Item | Area | Status |
|---|------|------|--------|
| 13 | Fix ModalService stub — implement with @cocoar/ui-overlay | Frontend | [x] |
| 14 | Replace `alert()` in passkey login error handling with proper signal-based errors | Frontend | [x] |

---

## P1 — Short Term

Important improvements for reliability and maintainability.

### P1.1 — Testing

| # | Item | Area | Status |
|---|------|------|--------|
| 15 | Add WebAuthn/FIDO2 integration tests | Tests | [ ] |
| 16 | Add frontend unit tests for guards (auth, admin, public, two-factor) | Frontend | [ ] |
| 17 | Add frontend unit tests for services (AuthApiService, AdminApiService, AuthStateService) | Frontend | [ ] |
| 18 | Add frontend unit tests for interceptors (credentials interceptor) | Frontend | [ ] |
| 19 | Add frontend E2E tests for critical flows (login, 2FA, password change, sessions) | Frontend | [ ] |
| 20 | Add code coverage reporting to CI (coverlet for .NET, vitest coverage for Angular) | CI/CD | [ ] |

### P1.2 — CI/CD Enhancements

| # | Item | Area | Status |
|---|------|------|--------|
| 21 | Add security scanning to CI (dependency vulnerability checks) | CI/CD | [ ] |
| 22 | Add Docker image build job to CI pipeline | CI/CD | [ ] |
| 23 | Integrate GitVersion for automatic semantic versioning in CI | CI/CD | [ ] |
| 24 | Add deployment pipeline (at least to staging) | CI/CD | [ ] |
| 25 | Fix CI: update .NET SDK version to 10.0 (currently 9.0 in workflow) | CI/CD | [ ] |
| 26 | Fix CI: use pnpm instead of npm for frontend job | CI/CD | [ ] |

### P1.3 — Documentation Fixes

| # | Item | Area | Status |
|---|------|------|--------|
| 27 | Update README.md — fix ".NET 9.0" references to 10.0, update test count to 157 | Docs | [ ] |
| 28 | Update PROJECT-STATUS.md — update metrics (157 tests, 50+ events, OAuth done) | Docs | [ ] |
| 29 | Update PROJECT-STATUS.md — mark OAuth/OpenIddict roadmap items as complete | Docs | [ ] |
| 30 | Update CLAUDE.md — add OAuth endpoints and OpenIddict dependencies | Docs | [ ] |
| 31 | Update Directory.Packages.props versions in PROJECT-STATUS.md (Marten 8.20, Wolverine 5.13, etc.) | Docs | [ ] |

---

## P2 — Medium Term

Improvements for operational excellence and scalability.

### P2.1 — Observability & Resilience

| # | Item | Area | Status |
|---|------|------|--------|
| 32 | Add metrics/observability (Prometheus, OpenTelemetry) | Infrastructure | [ ] |
| 33 | Add resilience patterns (Polly) for external calls (SMTP, etc.) | Infrastructure | [ ] |
| 34 | Add centralized logging sink (ELK, Seq, or cloud provider) | Infrastructure | [ ] |

### P2.2 — Security Enhancements

| # | Item | Area | Status |
|---|------|------|--------|
| 35 | Add CSRF protection for cookie-based auth | API | [ ] |
| 36 | Add request size limits | API | [ ] |
| 37 | Add brute-force detection/alerting beyond lockout | API | [ ] |
| 38 | Document secrets management strategy (vault integration) | Docs | [ ] |
| 39 | Replace external QR code API with local library (frontend) | Frontend | [ ] |

### P2.3 — Code Quality

| # | Item | Area | Status |
|---|------|------|--------|
| 40 | Add pre-commit hooks (husky + lint-staged for frontend) | Tooling | [ ] |
| 41 | Add commit message linting (commitlint) | Tooling | [ ] |
| 42 | Extract common test helpers (LoginAsAdminAsync, etc.) to shared base class | Tests | [ ] |
| 43 | Add .NET analyzers (StyleCop or SonarAnalyzer) | Backend | [ ] |

### P2.4 — Documentation

| # | Item | Area | Status |
|---|------|------|--------|
| 44 | Create deployment guide (Docker/K8s/cloud) | Docs | [ ] |
| 45 | Create configuration guide (all environment variables documented) | Docs | [ ] |
| 46 | Create security hardening guide | Docs | [ ] |
| 47 | Create OAuth client integration guide (how to connect a client app) | Docs | [ ] |
| 48 | Create backup & disaster recovery guide | Docs | [ ] |
| 49 | Expand SECURITY.md with incident response plan | Docs | [ ] |
| 50 | Create CHANGELOG entries for completed features | Docs | [ ] |

### P2.5 — Advanced Features

| # | Item | Area | Status |
|---|------|------|--------|
| 51 | Add API versioning strategy | API | [ ] |
| 52 | Add caching layer (Redis or in-memory) for read models | Infrastructure | [ ] |
| 53 | Add database backup/restore scripts | DevOps | [ ] |
| 54 | Add load testing setup (k6 or similar) | Tests | [ ] |

---

## Completed Items Log

Items are moved here with completion date when done.

| # | Item | Completed | Notes |
|---|------|-----------|-------|
| — | Remove Blazor frontend (`src/blazor-ui/`) | 2026-02-07 | Deleted manually, was fully isolated |
| — | Repository analysis and improvement tracking | 2026-02-07 | This document |
| 1 | Health check endpoint (`/health` with DB check) | 2026-02-08 | Already existed in codebase |
| 2 | API rate limiting | 2026-02-08 | Fixed-window: auth-strict (10/min), general (60/min) |
| 3 | Structured logging (Serilog) | 2026-02-08 | Serilog.AspNetCore with console sink |
| 4 | Security headers middleware | 2026-02-08 | CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy |
| 5 | Session ID from cookie | 2026-02-08 | Sessions created on login, stored in HttpOnly cookie, revoked on logout |
| 10 | OAuth authorization flow tests | 2026-02-08 | 6 tests: full roundtrip, PKCE enforcement, unauthenticated user, inactive user, userinfo, email scope |
| 11 | Token refresh flow tests | 2026-02-08 | 2 tests: valid refresh, inactive user refresh rejection |
| 12 | Client credentials flow tests | 2026-02-08 | 3 tests: valid secret, invalid secret, public client rejection. Also fixed 4 bugs: auth scheme, subject claim, double-hashed secrets, missing CC grant permission |
| 13 | Fix ModalService stub | 2026-02-08 | Rewritten to use CoarOverlayService.openComponent() with coarModalPreset, ModalHostUIService falls back to COAR_OVERLAY_REF |
| 14 | Replace alert() in passkey login | 2026-02-08 | Added AuthStateService.setError(), passkey errors now display via signal-bound coar-note |

---

## Notes

- Backend (Domain, Application, Infrastructure, API) is **feature-complete** including OAuth 2.0/OpenIdDict
- OAuth implementation is fully coded but **uncommitted** — should be committed as part of starting work
- Frontend covers 95% of backend endpoints with Angular 21 + Signals
- 168 backend integration tests with real PostgreSQL (Testcontainers)
- Frontend has near-zero test coverage (1 unit test, 1 E2E test)
