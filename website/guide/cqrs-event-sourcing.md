# CQRS & Event Sourcing

## CQRS with Wolverine

Commands and queries are dispatched via Wolverine's `IMessageBus`:

```csharp
var result = await _messageBus.InvokeAsync<ErrorOr<UserDto>>(command);
```

Handlers are static methods discovered automatically by Wolverine:

```csharp
public static async Task<ErrorOr<UserDto>> HandleAsync(
    CreateUserCommand command,
    IDocumentSession session,
    CancellationToken ct)
{
    // ...
}
```

## Event Sourcing

All user and role mutations are captured as domain events. Marten stores these in the `mt_events` table and projects them into read models.

### Domain Events (30+)

Examples:
- `UserCreated`, `UserNameChanged`, `UserEmailChanged`
- `UserPasswordChanged` (metadata only, no sensitive data)
- `UserLoggedIn`, `UserLoginFailed`
- `UserTwoFactorEnabled`, `UserTwoFactorDisabled`
- `RoleCreated`, `RoleUpdated`, `RoleDeleted`

### Projections

**Inline Projections** (synchronous, strong consistency):
- `UserState` — used by Identity stores for validation
- `RoleState` — used by RoleManager for lookups

**Async Projections** (eventually consistent):
- `UserDetailsReadModel` — rich read model for admin API responses

### Security Data Separation

Security-sensitive data (password hashes, authenticator keys) is stored in a separate `UserSecurityData` document, NOT in the event stream. Security events store metadata only.

## GDPR Support

Marten's built-in GDPR features:
- **Data Masking**: `AddMaskingRuleForProtectedInformation` masks PII in archived events
- **Stream Archiving**: `ArchiveStream` excludes deleted user data from queries
- Masking rules for: `UserCreated`, `UserNameChanged`, `UserEmailChanged`, `UserPhoneNumberChanged`, `UserProfileNameChanged`, `UserLoggedIn`, `UserLoginFailed`
