# Admin Endpoints

All admin endpoints require the `Admin` role. They work identically across all realms.

## Users

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/admin/users` | List users (paginated, searchable) |
| `GET` | `/admin/users/{id}` | Get user details |
| `POST` | `/admin/users` | Create user |
| `PATCH` | `/admin/users/{id}` | Update user |
| `DELETE` | `/admin/users/{id}` | Delete user |
| `POST` | `/admin/users/{id}/reset-password` | Reset user's password |
| `POST` | `/admin/users/{id}/unlock` | Unlock locked user |
| `GET` | `/admin/users/{id}/sessions` | List user's sessions |
| `DELETE` | `/admin/users/{id}/sessions` | Force logout user |
| `POST` | `/admin/users/{id}/soft-delete` | Soft delete (GDPR) |
| `POST` | `/admin/users/{id}/restore` | Restore soft-deleted user |
| `DELETE` | `/admin/users/{id}/permanent` | Permanent erasure (GDPR) |

### Query Parameters (GET /admin/users)

| Param | Type | Description |
|-------|------|-------------|
| `page` | int | Page number (1-based) |
| `pageSize` | int | Items per page |
| `search` | string | Search by username, email, name |
| `sortBy` | string | Sort field |
| `sortDescending` | bool | Sort direction |

## Roles

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/admin/roles` | List all roles |
| `GET` | `/admin/roles/{id}` | Get role |
| `POST` | `/admin/roles` | Create role |
| `PATCH` | `/admin/roles/{id}` | Update role |
| `DELETE` | `/admin/roles/{id}` | Delete role |
