# Harbor

Harbor is the internal HR application.

## Run the API

The API listens on http://localhost:5088.

1. Set `HARBOR_OWNER_CONNECTION` to the owner connection string.
2. Set `HARBOR_APP_CONNECTION` to the application connection string.
3. Run `dotnet run --project backend/Harbor.Host`.

`GET /api/healthz` returns `{"status":"ok"}`.

You can put the two connection strings in `backend/Harbor.Host/appsettings.Development.json`. That file stays out of git.

## SQL

SQL files are in `db/`. The host applies the files in name order when it starts. It uses the owner connection for SQL. It uses the application connection for queries.

## Secrets

Database passwords stay in `.secrets/dev-db.env`. Do not commit that directory. Do not put passwords in `appsettings.json`.
