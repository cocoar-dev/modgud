# API Reference

## Base URL

```
http://localhost:5000/api
```

## Authentication

The API uses cookie-based authentication. After successful login, an authentication cookie is set that must be included in subsequent requests.

---

## Auth Endpoints

### POST /auth/login

Authenticate a user and receive an authentication cookie.

**Request Body:**
```json
{
  "userName": "string",
  "password": "string"
}
```

**Response:** `200 OK`
```json
{
  "succeeded": true,
  "message": "Login successful"
}
```

**Error Responses:**
- `400 Bad Request` - Invalid credentials
- `403 Forbidden` - Account locked or email not confirmed

---

### POST /auth/logout

Sign out the current user and clear the authentication cookie.

**Authorization:** Required

**Response:** `200 OK`

---

### POST /auth/register

Register a new user account. Sends a confirmation email.

**Request Body:**
```json
{
  "userName": "string",
  "email": "string",
  "password": "string",
  "firstName": "string",
  "lastName": "string"
}
```

**Response:** `200 OK`
```json
{
  "message": "Registration successful. Please check your email to confirm your account."
}
```

**Error Responses:**
- `400 Bad Request` - Validation errors
- `409 Conflict` - Username or email already exists

---

### GET /auth/confirm-email

Confirm a user's email address using the token from the confirmation email.

**Query Parameters:**
- `userId` (required) - User ID
- `token` (required) - Confirmation token (URL-encoded)

**Response:** `200 OK`
```json
{
  "message": "Email confirmed successfully"
}
```

**Error Responses:**
- `400 Bad Request` - Invalid or expired token

---

### POST /auth/resend-confirmation

Resend the email confirmation link.

**Request Body:**
```json
{
  "email": "string"
}
```

**Response:** `200 OK`
```json
{
  "message": "If an account with that email exists and is not confirmed, a confirmation email has been sent."
}
```

---

### POST /auth/forgot-password

Request a password reset email.

**Request Body:**
```json
{
  "email": "string"
}
```

**Response:** `200 OK`
```json
{
  "message": "If an account with that email exists, a password reset link has been sent."
}
```

---

### POST /auth/reset-password

Reset password using the token from the password reset email.

**Request Body:**
```json
{
  "userId": "string",
  "token": "string",
  "newPassword": "string"
}
```

**Response:** `200 OK`
```json
{
  "message": "Password has been reset successfully"
}
```

**Error Responses:**
- `400 Bad Request` - Invalid or expired token

---

### GET /auth/me

Get current user information.

**Authorization:** Required

**Response:** `200 OK`
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "userName": "string",
  "email": "string",
  "roles": ["Admin", "User"]
}
```

---

### GET /auth/profile

Get current user's full profile.

**Authorization:** Required

**Response:** `200 OK`
```json
{
  "userName": "string",
  "email": "string",
  "firstName": "string",
  "lastName": "string",
  "phoneNumber": "string",
  "emailConfirmed": true,
  "phoneNumberConfirmed": false
}
```

---

### PUT /auth/profile

Update current user's profile.

**Authorization:** Required

**Request Body:**
```json
{
  "firstName": "string",
  "lastName": "string",
  "phoneNumber": "string"
}
```

**Response:** `200 OK`
```json
{
  "userName": "string",
  "email": "string",
  "firstName": "string",
  "lastName": "string",
  "phoneNumber": "string",
  "emailConfirmed": true,
  "phoneNumberConfirmed": false
}
```

---

### POST /auth/change-password

Change current user's password.

**Authorization:** Required

**Request Body:**
```json
{
  "currentPassword": "string",
  "newPassword": "string"
}
```

**Response:** `204 No Content`

**Error Responses:**
- `400 Bad Request` - Current password incorrect or new password doesn't meet requirements

---

## Admin - Users Endpoints

All admin endpoints require the `Admin` role.

### GET /admin/users

List all users with pagination and search.

**Authorization:** Admin role required

**Query Parameters:**
- `page` (optional, default: 1) - Page number
- `pageSize` (optional, default: 10) - Items per page
- `search` (optional) - Search term for username, email, or name

**Response:** `200 OK`
```json
{
  "items": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "userName": "string",
      "email": "string",
      "firstName": "string",
      "lastName": "string",
      "emailConfirmed": true,
      "isActive": true,
      "roles": ["Admin"]
    }
  ],
  "totalCount": 100,
  "page": 1,
  "pageSize": 10,
  "totalPages": 10
}
```

---

### GET /admin/users/{id}

Get a user by ID.

**Authorization:** Admin role required

**Response:** `200 OK`
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "userName": "string",
  "email": "string",
  "firstName": "string",
  "lastName": "string",
  "phoneNumber": "string",
  "emailConfirmed": true,
  "phoneNumberConfirmed": false,
  "twoFactorEnabled": false,
  "lockoutEnd": null,
  "lockoutEnabled": true,
  "accessFailedCount": 0,
  "isActive": true,
  "roles": ["Admin"]
}
```

**Error Responses:**
- `404 Not Found` - User not found

---

### POST /admin/users

Create a new user.

**Authorization:** Admin role required

**Request Body:**
```json
{
  "userName": "string",
  "email": "string",
  "password": "string",
  "firstName": "string",
  "lastName": "string",
  "roles": ["User"]
}
```

**Response:** `201 Created`
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "userName": "string",
  "email": "string",
  "firstName": "string",
  "lastName": "string",
  "roles": ["User"]
}
```

**Error Responses:**
- `400 Bad Request` - Validation errors
- `409 Conflict` - Username or email already exists

---

### PUT /admin/users/{id}

Update a user.

**Authorization:** Admin role required

**Request Body:**
```json
{
  "email": "string",
  "firstName": "string",
  "lastName": "string",
  "phoneNumber": "string",
  "isActive": true
}
```

**Response:** `200 OK`
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "userName": "string",
  "email": "string",
  "firstName": "string",
  "lastName": "string"
}
```

**Error Responses:**
- `404 Not Found` - User not found
- `409 Conflict` - Email already in use

---

### DELETE /admin/users/{id}

Delete a user.

**Authorization:** Admin role required

**Response:** `204 No Content`

**Error Responses:**
- `404 Not Found` - User not found

---

### POST /admin/users/{id}/reset-password

Reset a user's password (admin action).

**Authorization:** Admin role required

**Request Body:**
```json
{
  "newPassword": "string"
}
```

**Response:** `204 No Content`

**Error Responses:**
- `404 Not Found` - User not found
- `400 Bad Request` - Password doesn't meet requirements

---

### POST /admin/users/{id}/roles/{roleName}

Add a user to a role.

**Authorization:** Admin role required

**Response:** `204 No Content`

**Error Responses:**
- `404 Not Found` - User or role not found
- `400 Bad Request` - User already in role

---

### DELETE /admin/users/{id}/roles/{roleName}

Remove a user from a role.

**Authorization:** Admin role required

**Response:** `204 No Content`

**Error Responses:**
- `404 Not Found` - User or role not found
- `400 Bad Request` - User not in role

---

## Admin - Roles Endpoints

### GET /admin/roles

List all roles.

**Authorization:** Admin role required

**Response:** `200 OK`
```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "name": "Admin",
    "description": "Administrator role with full access"
  }
]
```

---

### GET /admin/roles/{id}

Get a role by ID.

**Authorization:** Admin role required

**Response:** `200 OK`
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Admin",
  "description": "Administrator role with full access"
}
```

**Error Responses:**
- `404 Not Found` - Role not found

---

### POST /admin/roles

Create a new role.

**Authorization:** Admin role required

**Request Body:**
```json
{
  "name": "string",
  "description": "string"
}
```

**Response:** `201 Created`
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "string",
  "description": "string"
}
```

**Error Responses:**
- `409 Conflict` - Role name already exists

---

### PUT /admin/roles/{id}

Update a role.

**Authorization:** Admin role required

**Request Body:**
```json
{
  "name": "string",
  "description": "string"
}
```

**Response:** `200 OK`
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "string",
  "description": "string"
}
```

**Error Responses:**
- `404 Not Found` - Role not found
- `409 Conflict` - Role name already exists

---

### DELETE /admin/roles/{id}

Delete a role.

**Authorization:** Admin role required

**Response:** `204 No Content`

**Error Responses:**
- `404 Not Found` - Role not found
- `400 Bad Request` - Cannot delete role with assigned users

---

## Error Response Format

All error responses follow the RFC 7807 Problem Details format:

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "One or more errors occurred",
  "status": 400,
  "errors": {
    "fieldName": ["Error message"]
  }
}
```

## HTTP Status Codes

| Code | Description |
|------|-------------|
| 200 | OK - Request succeeded |
| 201 | Created - Resource created |
| 204 | No Content - Request succeeded with no response body |
| 400 | Bad Request - Validation error or invalid input |
| 401 | Unauthorized - Authentication required |
| 403 | Forbidden - Insufficient permissions |
| 404 | Not Found - Resource not found |
| 409 | Conflict - Resource already exists |
| 500 | Internal Server Error - Unexpected error |
