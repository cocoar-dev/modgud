# Agent Safety Rules

## Stable Docker environment and PostgreSQL boundary (non-negotiable)

- The Docker containers `modgud` and `postgres` form a stable, shared integration environment used by other applications and agents. Never treat them as disposable or exclusively owned by the current task.
- Read-only diagnosis of the stable environment is allowed. This includes `docker ps`, `docker inspect`, `docker logs`, HTTP health checks, and provably read-only SQL queries.
- Without explicit user authorization in the current conversation, never write to PostgreSQL on host port `5432`. This prohibition includes migrations, schema changes, seeds, DML, cleanup, restores, and starting any alternate or locally built Modgud process that connects to it.
- Without explicit user authorization, never stop, start, restart, remove, recreate, replace, reconfigure, or rebuild the stable `modgud` or `postgres` containers. Do not change their images, networks, ports, volumes, environment, or connection strings.
- Never bind a development process to the stable container's port or otherwise route development traffic in a way that replaces or masks the stable instance.
- Do not copy, derive, or reuse a connection string from the running `modgud` container for development or testing.
- All local development, UI verification, manually started backend processes, migrations, and seeds must use the `postgres-dev` container on host port `5433` and a development database.
- Before starting a backend, verify from its resolved configuration that it targets `postgres-dev`/port `5433`. If this cannot be established, do not start it.
- If `postgres-dev` is unavailable or unsuitable, stop and ask the user. Never fall back to `postgres`/port `5432`.
- A running Docker application, known credentials, prior access, or a request to test the UI is not authorization to mutate the stable environment.
