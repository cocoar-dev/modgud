# Client Flows

This page explains the OAuth flows supported by Cocoar.Auth and when to use each one.

## Authorization Code + PKCE

**Use for:** SPAs, mobile apps, traditional web apps — anything where a user logs in.

```
1. App redirects user to:
   /{realm}/connect/authorize?
     response_type=code
     &client_id=my-app
     &redirect_uri=http://localhost:3000/callback
     &scope=openid profile email
     &code_challenge=...
     &code_challenge_method=S256

2. User logs in at Cocoar.Auth (+ 2FA if enabled)

3. User sees consent screen (if first time)

4. Cocoar.Auth redirects back to:
   http://localhost:3000/callback?code=AUTH_CODE

5. App exchanges code for tokens:
   POST /{realm}/connect/token
   {
     grant_type: authorization_code,
     code: AUTH_CODE,
     client_id: my-app,
     redirect_uri: http://localhost:3000/callback,
     code_verifier: ORIGINAL_VERIFIER
   }

6. Response:
   {
     access_token: "CfDJ8...",      // Reference token (opaque)
     id_token: "eyJhbG...",         // JWT with user claims
     refresh_token: "CfDJ8O...",    // If offline_access scope
     token_type: "Bearer",
     expires_in: 3600
   }
```

::: tip PKCE is Required
All authorization code requests must include `code_challenge` and `code_challenge_method=S256`. This protects against authorization code interception attacks, especially for public clients (SPAs, mobile apps).
:::

## Client Credentials

**Use for:** Machine-to-machine communication where no user is involved.

```
POST /{realm}/connect/token
{
  grant_type: client_credentials,
  client_id: billing-service,
  client_secret: SECRET,
  scope: billing-api
}

Response:
{
  access_token: "CfDJ8...",
  token_type: "Bearer",
  expires_in: 3600
}
```

No user context — the token represents the service itself.

## Refresh Token

**Use for:** Renewing expired access tokens without re-authentication.

```
POST /{realm}/connect/token
{
  grant_type: refresh_token,
  client_id: my-app,
  refresh_token: REFRESH_TOKEN
}

Response:
{
  access_token: "NEW_ACCESS_TOKEN",
  refresh_token: "NEW_REFRESH_TOKEN",    // Token rotation
  token_type: "Bearer",
  expires_in: 3600
}
```

::: info Token Rotation
Each refresh token use issues a new refresh token and invalidates the old one. This limits the damage if a refresh token is stolen.
:::

## Token Introspection

**Use for:** Resource servers validating access tokens.

```
POST /{realm}/connect/introspect
Authorization: Basic base64(resource-id:resource-secret)
{
  token: ACCESS_TOKEN,
  token_type_hint: access_token
}

Response (active token):
{
  active: true,
  sub: "user-id",
  client_id: "my-app",
  scope: "openid profile email",
  aud: "billing-api",
  exp: 1742000000
}

Response (revoked/expired token):
{
  active: false
}
```

## Token Revocation

```
POST /{realm}/connect/revocation
{
  client_id: my-app,
  client_secret: SECRET,        // For confidential clients
  token: TOKEN_TO_REVOKE,
  token_type_hint: access_token  // or refresh_token
}
```

Revocation is immediate — the next introspection call will return `active: false`.

## Device Code Flow

**Use for:** Devices with limited input capabilities (smart TVs, CLI tools, IoT devices) where the user authenticates on a separate device.

```
1. Device requests authorization:
   POST /{realm}/connect/deviceauthorization
   {
     client_id: my-cli-tool,
     scope: openid profile
   }

   Response:
   {
     device_code: "DEVICE_CODE",
     user_code: "XXXX-XXXX",
     verification_uri: "https://auth.example.com/{realm}/device",
     expires_in: 600,
     interval: 5
   }

2. Device shows the user code and verification URL

3. User opens the verification URL in their browser
   → Logs in (if not already)
   → Enters the user code (XXXX-XXXX)
   → Clicks "Approve"

4. Device polls for the token:
   POST /{realm}/connect/token
   {
     grant_type: urn:ietf:params:oauth:grant-type:device_code,
     client_id: my-cli-tool,
     device_code: DEVICE_CODE
   }

   While pending:
   { error: "authorization_pending" }

   After user approves:
   {
     access_token: "CfDJ8...",
     id_token: "eyJhbG...",
     token_type: "Bearer",
     expires_in: 3600
   }
```

::: tip User Code Format
The user code is displayed as `XXXX-XXXX` (8 characters with a dash). The user enters it on the verification page in their browser. The code expires after 10 minutes.
:::

## Per-Realm Endpoints

All OAuth endpoints are realm-scoped. The realm slug is always the first path segment:

| Example |
|---------|
| `/{slug}/connect/authorize` |
| `/{slug}/connect/token` |
| `/{slug}/connect/introspect` |
| `/{slug}/connect/revocation` |
| `/{slug}/connect/deviceauthorization` |
| `/{slug}/connect/verify` |
| `/{slug}/.well-known/openid-configuration` |

The system realm uses `/system/` as its slug: `/system/connect/token`, `/system/.well-known/openid-configuration`, etc.
