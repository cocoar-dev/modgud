# Endpoint Mapping

This document provides a comprehensive overview of all API endpoints and their implementation patterns (Service vs CQRS/Command), showing which endpoints emit domain events.

## Legend

| Symbol | Meaning |
|--------|---------|
| ✅ | Implemented / Uses this pattern |
| ❌ | Not implemented / Does not use this pattern |
| 🔶 | Partial (some operations emit events) |

---

## Summary by Pattern

| Pattern | Count | Description |
|---------|-------|-------------|
| **Service-based (no CQRS)** | 11 | Direct service calls, no command/query handlers |
| **CQRS with Commands** | 8 | Uses Wolverine commands with handlers |
| **CQRS with Queries** | 4 | Uses Wolverine queries with handlers |
| **Emits Events** | 11 | Operations that append to event stream |
| **No Events** | 12 | Operations that don't emit domain events |

---

## Auth Endpoints (`/api/auth`)

These endpoints use **service-based** architecture (no CQRS commands).

| Method | Endpoint | Service | CQRS | Events | Events Emitted |
|--------|----------|---------|------|--------|----------------|
| POST | `/login` | `AuthService.LoginAsync` | ❌ | ❌ | - |
| POST | `/logout` | `AuthService.LogoutAsync` | ❌ | ❌ | - |
| POST | `/register` | `AuthService.RegisterAsync` | ❌ | ✅ | `UserCreated` (via UserManager → EventSourcedUserStore) |
| GET | `/confirm-email` | `AuthService.ConfirmEmailAsync` | ❌ | ✅ | `UserEmailConfirmed` (via UserManager → EventSourcedUserStore) |
| POST | `/resend-confirmation` | `AuthService.ResendConfirmationEmailAsync` | ❌ | ❌ | - |
| POST | `/forgot-password` | `AuthService.ForgotPasswordAsync` | ❌ | ❌ | - |
| POST | `/reset-password` | `AuthService.ResetPasswordAsync` | ❌ | ✅ | `UserPasswordChanged` (via UserManager → EventSourcedUserStore) |
| GET | `/me` | `AuthService.GetCurrentUserAsync` | ❌ | ❌ | - (read-only) |
| GET | `/profile` | `AuthService.GetProfileAsync` | ❌ | ❌ | - (read-only) |
| PUT | `/profile` | `AuthService.UpdateProfileAsync` | ❌ | ✅ | `UserProfileNameChanged`, `UserPhoneNumberChanged`, etc. (via UserManager → EventSourcedUserStore) |
| POST | `/change-password` | `UserService.ChangePasswordAsync` | ❌ | ✅ | `UserPasswordChanged` (via UserManager → EventSourcedUserStore) |

---

## Admin User Endpoints (`/api/admin/users`)

These endpoints use **CQRS pattern** with Wolverine.

| Method | Endpoint | Command/Query | Handler | Events | Events Emitted |
|--------|----------|---------------|---------|--------|----------------|
| GET | `/` | `GetUsersPagedQuery` | `GetUsersPagedHandler` | ❌ | - (read-only) |
| GET | `/{id}` | `GetUserByIdQuery` | `GetUserByIdHandler` | ❌ | - (read-only) |
| POST | `/` | `CreateUserCommand` | `CreateUserHandler` | ✅ | `UserCreated` (via UserManager → EventSourcedUserStore) |
| PATCH | `/{id}` | `UpdateUserCommand` | `UpdateUserHandler` | ✅ | `UserNameChanged`, `UserEmailChanged`, `UserPhoneNumberChanged`, `UserProfileNameChanged`, `UserActivated`/`UserDeactivated`, `UserRoleAssigned`/`UserRoleRemoved`, etc. (via UserManager → EventSourcedUserStore) |
| DELETE | `/{id}` | `DeleteUserCommand` | `DeleteUserHandler` | ✅ | `UserDeleted` (via UserManager → EventSourcedUserStore) |
| POST | `/{id}/reset-password` | `ResetUserPasswordCommand` | `ResetUserPasswordHandler` | ✅ | `UserPasswordChanged` (via UserManager → EventSourcedUserStore) |

---

## Admin Role Endpoints (`/api/admin/roles`)

These endpoints use **CQRS pattern** with Wolverine and emit domain events via `EventSourcedRoleStore`.

| Method | Endpoint | Command/Query | Handler | Events | Events Emitted |
|--------|----------|---------------|---------|--------|----------------|
| GET | `/` | `GetAllRolesQuery` | `GetAllRolesHandler` | ❌ | - (read-only) |
| GET | `/{id}` | `GetRoleByIdQuery` | `GetRoleByIdHandler` | ❌ | - (read-only) |
| POST | `/` | `CreateRoleCommand` | `CreateRoleHandler` | ✅ | `RoleCreated` (via RoleManager → EventSourcedRoleStore) |
| PATCH | `/{id}` | `UpdateRoleCommand` | `UpdateRoleHandler` | ✅ | `RoleNameChanged`, `RoleDescriptionChanged`, `RoleClaimAdded`, `RoleClaimRemoved` (via RoleManager → EventSourcedRoleStore) |
| DELETE | `/{id}` | `DeleteRoleCommand` | `DeleteRoleHandler` | ✅ | `RoleDeleted` (via RoleManager → EventSourcedRoleStore) |

---

## Event Flow Architecture

### Event-Sourced Endpoints (Users)

```
Endpoint → Command/Service → UserManager → EventSourcedUserStore → Marten Event Stream
                                                    ↓
                                        UserStateProjection (inline)
                                                    ↓
                                              UserState
```

### Event-Sourced Endpoints (Roles)

```
Endpoint → Command → RoleManager → EventSourcedRoleStore → Marten Event Stream
                                              ↓
                                   RoleStateProjection (inline)
                                              ↓
                                        RoleState
```

---

## Domain Events Reference

### User Profile Events (contain data)

| Event | Emitted By | Description |
|-------|------------|-------------|
| `UserCreated` | CreateUser, Register | Initial user creation |
| `UserNameChanged` | UpdateUser | Username modification |
| `UserEmailChanged` | UpdateUser, UpdateProfile | Email address change |
| `UserPhoneNumberChanged` | UpdateUser, UpdateProfile | Phone number change |
| `UserProfileNameChanged` | UpdateUser, UpdateProfile | First/Last name change |
| `UserActivated` | UpdateUser | User activation |
| `UserDeactivated` | UpdateUser | User deactivation |
| `UserDeleted` | DeleteUser | Soft delete |
| `UserRoleAssigned` | UpdateUser | Role assignment |
| `UserRoleRemoved` | UpdateUser | Role removal |

### User Security Events (metadata only)

| Event | Emitted By | Description |
|-------|------------|-------------|
| `UserPasswordChanged` | ResetPassword, ChangePassword | Password change (timestamp only, no hash) |
| `UserEmailConfirmed` | ConfirmEmail | Email confirmation |
| `UserPhoneNumberConfirmed` | UpdateUser | Phone confirmation |
| `UserLockedOut` | Login (after failures) | Account lockout |
| `UserUnlocked` | UpdateUser | Lockout release |
| `UserTwoFactorEnabled` | UpdateUser | 2FA enabled |
| `UserTwoFactorDisabled` | UpdateUser | 2FA disabled |

### Role Events

| Event | Emitted By | Description |
|-------|------------|-------------|
| `RoleCreated` | CreateRole | Initial role creation with name, description, claims |
| `RoleNameChanged` | UpdateRole | Role name modification |
| `RoleDescriptionChanged` | UpdateRole | Role description modification |
| `RoleClaimAdded` | UpdateRole (via claims) | New claim added to role |
| `RoleClaimRemoved` | UpdateRole (via claims) | Claim removed from role |
| `RoleDeleted` | DeleteRole | Role deletion |

---

## Architecture Summary

### Naming Convention
- **`*State`**: Inline projections for validation and Identity (e.g., `UserState`, `RoleState`)
- **`*ReadModel`**: Async projections for API display (e.g., `UserDetailsReadModel`)

### Current State
- **Users**: Event-sourced via `EventSourcedUserStore` with `UserStateProjection`
- **Roles**: Event-sourced via `EventSourcedRoleStore` with `RoleStateProjection`
- **Auth endpoints**: Service-based, events emitted via UserManager calls

### Future Considerations
1. **Audit Logging**: Extend events to include actor/admin information
2. **Auth Events**: Consider adding login/logout events for security audit trail
3. **Async Projections**: Consider moving to async projections for high-traffic scenarios

---

## Quick Reference: What Has Events?

| Category | Has Events | Notes |
|----------|------------|-------|
| User CRUD (Admin) | ✅ | All operations emit events via EventSourcedUserStore |
| User Registration | ✅ | Emits `UserCreated` |
| User Profile Updates | ✅ | Emits various profile change events |
| Password Operations | ✅ | Emits `UserPasswordChanged` (metadata only) |
| Email Confirmation | ✅ | Emits `UserEmailConfirmed` |
| Role CRUD (Admin) | ✅ | All operations emit events via EventSourcedRoleStore |
| Login/Logout | ❌ | No events (consider adding for audit) |
| Resend Confirmation | ❌ | No state change, just sends email |
| Forgot Password | ❌ | No state change, just sends email |
