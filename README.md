# Harbor

Harbor is the internal HR application.

## Run

Postgres stays on 127.0.0.1:5432. Put the two connection strings in `backend/Harbor.Host/appsettings.Development.json`. That file stays out of git.

- `make` starts the API at http://localhost:5088 and the web app at http://localhost:5190.
- `make seed` prints a link. Open the link to create a passkey for ew@eliaswitt.com.

`GET /api/healthz` returns `{"status":"ok"}`.

## SQL

SQL files are in `db/`. The host applies the files in name order when it starts. It uses the owner connection for SQL. It uses the application connection for queries.

## Secrets

Database passwords stay in `.secrets/dev-db.env`. Do not commit that directory. Do not put passwords in `appsettings.json`.
