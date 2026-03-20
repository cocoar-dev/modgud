# OAuth Endpoints

OAuth/OIDC admin endpoints for managing clients, scopes, and API resources. All require the `Admin` role and work per-realm.

## Clients

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/admin/oauth/clients` | List clients (paginated) |
| `GET` | `/admin/oauth/clients/{id}` | Get client |
| `POST` | `/admin/oauth/clients` | Create client |
| `PUT` | `/admin/oauth/clients/{id}` | Update client |
| `DELETE` | `/admin/oauth/clients/{id}` | Delete client |
| `POST` | `/admin/oauth/clients/{id}/regenerate-secret` | Regenerate client secret |

## Scopes

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/admin/oauth/scopes` | List scopes |
| `GET` | `/admin/oauth/scopes/{id}` | Get scope |
| `POST` | `/admin/oauth/scopes` | Create scope |
| `PUT` | `/admin/oauth/scopes/{id}` | Update scope |
| `DELETE` | `/admin/oauth/scopes/{id}` | Delete scope |

## APIs

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/admin/oauth/api-resources` | List API resources (paginated) |
| `GET` | `/admin/oauth/api-resources/{id}` | Get API resource |
| `POST` | `/admin/oauth/api-resources` | Create API resource |
| `PUT` | `/admin/oauth/api-resources/{id}` | Update API resource |
| `DELETE` | `/admin/oauth/api-resources/{id}` | Delete API resource |
| `POST` | `/admin/oauth/api-resources/{id}/regenerate-secret` | Regenerate API secret |

## OpenID Connect Discovery

Each realm has its own OIDC discovery endpoint:

- `/{slug}/.well-known/openid-configuration` (e.g. `/system/.well-known/openid-configuration`, `/acme/.well-known/openid-configuration`)

The issuer URL is realm-specific, ensuring tokens from one realm are not valid in another.
