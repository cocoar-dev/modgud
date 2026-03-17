# OAuth & OpenID Connect

## Overview

Cocoar.Auth is a full OAuth 2.0 Authorization Server and OpenID Connect Provider. It allows external applications to authenticate users and access protected APIs.

For terminology (Client, Scope, API, Grant Type, Token Types) see the [Glossary](/concepts/glossary).

## How It Fits Together

There are three actors in every OAuth flow:

| Actor | Role | Example |
|-------|------|---------|
| **User** | The person logging in | Someone using your app |
| **Client** | The application requesting access | Your SPA, mobile app, or backend service |
| **API** | The protected service being accessed | Your billing service, order service, etc. |

Cocoar.Auth sits in the middle — it authenticates the user, issues tokens to the client, and the API validates those tokens.

## Supported Flows

### Authorization Code (for user-facing apps)

The standard flow for web apps, SPAs, and mobile apps. The user logs in at Cocoar.Auth and the app receives tokens.

See [Client Flows](/user-guide/client-flows) for detailed step-by-step examples.

### Client Credentials (for services)

For machine-to-machine communication where no user is involved. The service authenticates directly with its client ID and secret.

## Token Validation

How an API validates an access token depends on the token type configured for the client:

| Token Type | How the API validates |
|-----------|----------------------|
| **Reference Token** (default) | Calls Cocoar.Auth's introspection endpoint — gets back user info, scopes, expiry. Token can be instantly revoked. |
| **JWT** | Validates the signature locally using the signing key from the OIDC discovery endpoint. No call to Cocoar.Auth needed, but revocation only works on expiry. |

See [Glossary > Access Token Types](/concepts/glossary#access-token-types) for when to use which.

## Per-Realm Isolation

Each realm has its own independent OAuth configuration:

- Clients registered in one realm cannot authenticate against another realm
- Tokens issued by one realm are invalid in another
- Each realm has its own OIDC discovery endpoint
- The issuer claim in tokens includes the realm URL

This means two realms can each have a client called `my-app` — they are completely independent.
