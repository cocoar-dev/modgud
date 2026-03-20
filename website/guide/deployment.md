# Docker & Deployment

::: info Work in Progress
Detailed deployment documentation will be added as the project matures.
:::

## Docker Compose (Development)

```yaml
services:
  postgres:
    image: postgres:17
    container_name: cocoar-postgres
    ports:
      - "5432:5432"
    environment:
      POSTGRES_PASSWORD: postgres
    volumes:
      - pgdata:/var/lib/postgresql/data

volumes:
  pgdata:
```

## Production Considerations

### Nginx Reverse Proxy

The SPA must be served for realm paths:

```nginx
# Proxy API/connect/discovery requests to backend
location ~ ^/[a-z][a-z0-9-]+/(api|connect|\.well-known) {
  proxy_pass http://backend:80;
}

# Redirect root to system realm
location = / {
  return 302 /system/;
}

# Serve SPA for all realm navigation paths
location ~ ^/[a-z][a-z0-9-]+/ {
  try_files $uri /index.html;
}
```

### Database Setup

On first start, the backend automatically creates:
- `cocoar_auth_master` — Marten tenant registry
- `cocoar_auth_system` — System realm database

Additional realm databases are created on demand when realms are provisioned through the admin API.
