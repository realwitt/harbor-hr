#!/bin/sh
# Create the runtime login before the owner applies db/*.sql.
# Runs once, on the first Postgres data directory init.
set -eu
: "${HARBOR_APP_PASSWORD:?HARBOR_APP_PASSWORD is required}"
: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=app_password="$HARBOR_APP_PASSWORD" <<'SQL'
select case
  when exists (select 1 from pg_catalog.pg_roles where rolname = 'harbor_app')
  then format('alter role harbor_app with login password %L', :'app_password')
  else format('create role harbor_app login password %L', :'app_password')
end
\gexec
grant connect on database harbor to harbor_app;
SQL
