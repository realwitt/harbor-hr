-- Audit trail. Triggers write the rows. Application code does not.
-- search_path on the writer functions is locked to audit, pg_catalog, pg_temp.

create schema if not exists audit;

do $enum$
begin
  create type audit.operation as enum ('INSERT', 'UPDATE', 'DELETE');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type audit.channel as enum ('web', 'mcp', 'job', 'system');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type audit.auth_factor as enum ('webauthn', 'recovery_code', 'session', 'system');
exception when duplicate_object then null;
end
$enum$;

create table if not exists audit.record_version (
  id              bigserial primary key,
  record_id       text not null,
  op              audit.operation not null,
  ts              timestamptz not null default now(),
  table_oid       oid not null,
  table_schema    name not null,
  table_name      name not null,
  record          jsonb,
  old_record      jsonb,
  changed_fields  text[],
  actor_id        uuid,
  channel         audit.channel not null default 'system',
  client_id       uuid,
  auth_factor     audit.auth_factor not null default 'system',
  request_id      text,
  quote_id        uuid,
  idempotency_key text
);

create index if not exists record_version_ts_brin
  on audit.record_version using brin (ts);
create index if not exists record_version_row
  on audit.record_version (table_schema, table_name, record_id);
create index if not exists record_version_actor
  on audit.record_version (actor_id, ts desc);
create index if not exists record_version_client
  on audit.record_version (client_id, ts desc)
  where client_id is not null;

create table if not exists audit.access_event (
  id           bigserial primary key,
  at           timestamptz not null default now(),
  actor_id     uuid,
  action       text not null,
  outcome      text not null,
  channel      audit.channel not null,
  client_id    uuid,
  auth_factor  audit.auth_factor not null,
  subject_id   uuid,
  quote_id     uuid,
  tool_name    text,
  request_id   text,
  detail       jsonb,
  check (outcome in ('ok', 'denied'))
);

create index if not exists access_event_actor
  on audit.access_event (actor_id, at desc);
create index if not exists access_event_action
  on audit.access_event (action, at desc);

-- Empty string is null. A bad uuid raises. Callers pass the raw GUC text.
create or replace function audit.parse_uuid(raw text)
returns uuid
language plpgsql
immutable
set search_path = audit, pg_catalog, pg_temp
as $$
begin
  if raw is null or raw = '' then
    return null;
  end if;
  return raw::uuid;
end
$$;

-- Unknown labels fall back to system. Empty string is null, then system.
create or replace function audit.parse_channel(raw text)
returns audit.channel
language plpgsql
immutable
set search_path = audit, pg_catalog, pg_temp
as $$
begin
  if raw is null or raw = '' then
    return 'system';
  end if;
  begin
    return raw::audit.channel;
  exception
    when invalid_text_representation then
      return 'system';
  end;
end
$$;

create or replace function audit.parse_auth_factor(raw text)
returns audit.auth_factor
language plpgsql
immutable
set search_path = audit, pg_catalog, pg_temp
as $$
begin
  if raw is null or raw = '' then
    return 'system';
  end if;
  begin
    return raw::audit.auth_factor;
  exception
    when invalid_text_representation then
      return 'system';
  end;
end
$$;

create or replace function audit.read_context(
  out actor_id uuid,
  out channel audit.channel,
  out client_id uuid,
  out auth_factor audit.auth_factor,
  out request_id text,
  out quote_id uuid,
  out idempotency_key text
)
language plpgsql
stable
set search_path = audit, pg_catalog, pg_temp
as $$
begin
  actor_id := audit.parse_uuid(current_setting('audit.actor_id', true));
  client_id := audit.parse_uuid(current_setting('audit.client_id', true));
  quote_id := audit.parse_uuid(current_setting('audit.quote_id', true));
  request_id := nullif(current_setting('audit.request_id', true), '');
  idempotency_key := nullif(current_setting('audit.idempotency_key', true), '');
  channel := audit.parse_channel(current_setting('audit.channel', true));
  auth_factor := audit.parse_auth_factor(current_setting('audit.auth_factor', true));
end
$$;

-- Replace secret values with the JSON string "[redacted]". Match is case-insensitive.
-- Top-level keys only. A secret object is replaced as a whole.
create or replace function audit.redact_document(doc jsonb)
returns jsonb
language sql
immutable
set search_path = audit, pg_catalog, pg_temp
as $$
  select case
    when doc is null then null
    else (
      select coalesce(
        jsonb_object_agg(
          e.key,
          case
            when lower(e.key) in (
              'code_hash',
              'token_hash',
              'challenge',
              'client_secret',
              'json_web_key_set',
              'payload'
            )
            then to_jsonb('[redacted]'::text)
            else e.value
          end
        ),
        '{}'::jsonb
      )
      from jsonb_each(doc) as e(key, value)
    )
  end
$$;

create or replace function audit.mask_session_id(doc jsonb)
returns jsonb
language sql
immutable
set search_path = audit, pg_catalog, pg_temp
as $$
  select case
    when doc is null then null
    when jsonb_exists(doc, 'id')
      then jsonb_set(doc, '{id}', to_jsonb('[redacted]'::text), false)
    else doc
  end
$$;

-- app_session record_id is sha256(id text) hex. Other tables use id, or the primary key.
create or replace function audit.record_identity(
  schema_name name,
  table_name name,
  relid oid,
  row_data jsonb
)
returns text
language plpgsql
stable
set search_path = audit, pg_catalog, pg_temp
as $$
declare
  rid text;
begin
  if schema_name = 'public' and table_name = 'app_session' then
    return encode(sha256(convert_to(row_data->>'id', 'UTF8')), 'hex');
  end if;

  if jsonb_exists(row_data, 'id') then
    return row_data->>'id';
  end if;

  select string_agg(row_data->>a.attname, ':' order by k.ord)
    into rid
  from pg_index i
  cross join lateral unnest(i.indkey) with ordinality as k(attnum, ord)
  join pg_attribute a
    on a.attrelid = i.indrelid
   and a.attnum = k.attnum
  where i.indrelid = relid
    and i.indisprimary
    and k.attnum > 0
    and not a.attisdropped;

  if rid is null or rid = '' then
    raise exception 'audit record_id missing for %.%', schema_name, table_name;
  end if;

  return rid;
end
$$;

create or replace function audit.record_change()
returns trigger
language plpgsql
security definer
set search_path = audit, pg_catalog, pg_temp
as $$
declare
  new_row jsonb;
  old_row jsonb;
  diff_new jsonb;
  diff_old jsonb;
  fields text[];
  rid text;
begin
  if tg_op = 'DELETE' then
    old_row := to_jsonb(old);
  else
    new_row := to_jsonb(new);
    if tg_op = 'UPDATE' then
      old_row := to_jsonb(old);
    end if;
  end if;

  rid := audit.record_identity(
    tg_table_schema,
    tg_table_name,
    tg_relid,
    coalesce(new_row, old_row)
  );

  if tg_op = 'UPDATE' then
    select
      jsonb_object_agg(n.key, n.value),
      jsonb_object_agg(n.key, o.value),
      array_agg(n.key order by n.key)
    into diff_new, diff_old, fields
    from jsonb_each(new_row) as n(key, value)
    left join jsonb_each(old_row) as o(key, value) on o.key = n.key
    where n.value is distinct from o.value
      and n.key not in ('created_at', 'updated_at');

    if diff_new is null then
      return new;
    end if;

    diff_new := audit.redact_document(diff_new);
    diff_old := audit.redact_document(diff_old);
    if tg_table_schema = 'public' and tg_table_name = 'app_session' then
      diff_new := audit.mask_session_id(diff_new);
      diff_old := audit.mask_session_id(diff_old);
    end if;

    insert into audit.record_version (
      record_id, op, table_oid, table_schema, table_name,
      record, old_record, changed_fields,
      actor_id, channel, client_id, auth_factor,
      request_id, quote_id, idempotency_key
    )
    select
      rid, 'UPDATE'::audit.operation, tg_relid, tg_table_schema, tg_table_name,
      diff_new, diff_old, fields,
      c.actor_id, c.channel, c.client_id, c.auth_factor,
      c.request_id, c.quote_id, c.idempotency_key
    from audit.read_context() as c;

    return new;
  end if;

  if tg_op = 'INSERT' then
    new_row := audit.redact_document(new_row);
    if tg_table_schema = 'public' and tg_table_name = 'app_session' then
      new_row := audit.mask_session_id(new_row);
    end if;

    insert into audit.record_version (
      record_id, op, table_oid, table_schema, table_name,
      record, old_record, changed_fields,
      actor_id, channel, client_id, auth_factor,
      request_id, quote_id, idempotency_key
    )
    select
      rid, 'INSERT'::audit.operation, tg_relid, tg_table_schema, tg_table_name,
      new_row, null, null,
      c.actor_id, c.channel, c.client_id, c.auth_factor,
      c.request_id, c.quote_id, c.idempotency_key
    from audit.read_context() as c;

    return new;
  end if;

  old_row := audit.redact_document(old_row);
  if tg_table_schema = 'public' and tg_table_name = 'app_session' then
    old_row := audit.mask_session_id(old_row);
  end if;

  insert into audit.record_version (
    record_id, op, table_oid, table_schema, table_name,
    record, old_record, changed_fields,
    actor_id, channel, client_id, auth_factor,
    request_id, quote_id, idempotency_key
  )
  select
    rid, 'DELETE'::audit.operation, tg_relid, tg_table_schema, tg_table_name,
    null, old_row, null,
    c.actor_id, c.channel, c.client_id, c.auth_factor,
    c.request_id, c.quote_id, c.idempotency_key
  from audit.read_context() as c;

  return old;
end
$$;

-- The only insert path for access_event. Outcome is ok or denied.
create or replace function audit.record_access(
  action text,
  outcome text,
  subject_id uuid,
  tool_name text,
  detail jsonb
)
returns void
language plpgsql
security definer
set search_path = audit, pg_catalog, pg_temp
as $$
begin
  if outcome is distinct from 'ok' and outcome is distinct from 'denied' then
    raise exception 'outcome must be ok or denied';
  end if;

  insert into audit.access_event (
    actor_id, action, outcome, channel, client_id, auth_factor,
    subject_id, quote_id, tool_name, request_id, detail
  )
  select
    c.actor_id, action, outcome, c.channel, c.client_id, c.auth_factor,
    subject_id, c.quote_id, tool_name, c.request_id, detail
  from audit.read_context() as c;
end
$$;

create or replace function audit.reject_change()
returns trigger
language plpgsql
set search_path = pg_catalog, pg_temp
as $$
begin
  raise exception '% is append-only', tg_table_schema || '.' || tg_table_name;
end
$$;

drop trigger if exists record_version_append_only on audit.record_version;
create trigger record_version_append_only
before update or delete on audit.record_version
for each row execute function audit.reject_change();

drop trigger if exists access_event_append_only on audit.access_event;
create trigger access_event_append_only
before update or delete on audit.access_event
for each row execute function audit.reject_change();

-- Create audit_record_change when it is missing. Do not replace an existing trigger.
create or replace function audit.enable_tracking(target_table regclass)
returns void
language plpgsql
set search_path = audit, pg_catalog, pg_temp
as $$
begin
  if exists (
    select 1
    from pg_trigger
    where tgrelid = target_table
      and tgname = 'audit_record_change'
      and not tgisinternal
  ) then
    return;
  end if;

  execute format(
    'create trigger audit_record_change '
    'after insert or update or delete on %s '
    'for each row execute function audit.record_change()',
    target_table
  );
end
$$;

do $track$
declare
  rel regclass;
begin
  for rel in
    select c.oid::regclass
    from pg_class c
    join pg_namespace n on n.oid = c.relnamespace
    where n.nspname = 'public'
      and c.relkind = 'r'
  loop
    perform audit.enable_tracking(rel);
  end loop;
end
$track$;
