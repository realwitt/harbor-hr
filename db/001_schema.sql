-- Harbor application schema. Money is int cents. Leave is numeric(8,2) hours.
-- Time is timestamptz. There is no balance column and no paycheck column.

create extension if not exists pgcrypto;
create extension if not exists btree_gist;

do $enum$
begin
  create type public.grant_model as enum ('accrued', 'instant', 'unpaid');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type public.leave_status as enum ('pending', 'approved', 'denied', 'cancelled');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type public.ledger_kind as enum ('grant', 'accrual', 'usage', 'release', 'adjustment', 'expiration');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type public.deduction_kind as enum ('hsa', 'health_fsa', 'dependent_care_fsa');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type public.election_status as enum ('pending_confirm', 'active', 'ended');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type public.quote_kind as enum ('leave', 'deduction');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type public.employee_role as enum ('employee', 'hr_admin');
exception when duplicate_object then null;
end
$enum$;

do $enum$
begin
  create type public.hsa_coverage as enum ('self', 'family');
exception when duplicate_object then null;
end
$enum$;

create table if not exists public.employee (
  id                uuid primary key default gen_random_uuid(),
  email             text not null unique,
  name              text not null,
  role              public.employee_role not null default 'employee',
  timezone          text not null default 'America/New_York',
  jurisdiction      text not null,
  hired_on          date not null,
  terminated_on     date,
  born_on           date,
  hdhp_eligible     boolean not null default false,
  hsa_coverage      public.hsa_coverage,
  recovery_saved_at timestamptz,
  mcp_enabled_at    timestamptz,
  created_at        timestamptz not null default now(),
  check (hsa_coverage is null or hdhp_eligible),
  check (terminated_on is null or terminated_on >= hired_on)
);

-- Manager is a relationship, not a role. One open link per employee.
create table if not exists public.manager_link (
  employee_id  uuid not null references public.employee (id),
  manager_id   uuid not null references public.employee (id),
  effective_on date not null,
  ended_on     date,
  primary key (employee_id, effective_on),
  check (employee_id <> manager_id),
  check (ended_on is null or ended_on >= effective_on)
);

create unique index if not exists manager_link_one_open
  on public.manager_link (employee_id)
  where ended_on is null;

create table if not exists public.pay_period (
  id         uuid primary key default gen_random_uuid(),
  starts_on  date not null,
  ends_on    date not null,
  pay_date   date not null,
  unique (starts_on, ends_on),
  check (starts_on <= ends_on),
  constraint pay_period_no_overlap
    exclude using gist (daterange(starts_on, ends_on, '[]') with &&)
);

create table if not exists public.company_holiday (
  on_date  date primary key,
  name     text not null
);

create table if not exists public.leave_type (
  id                uuid primary key default gen_random_uuid(),
  code              text not null unique,
  model             public.grant_model not null,
  hours_per_grant   numeric(8,2),
  year_boundary     text not null default 'calendar',
  carry_cap_hours   numeric(8,2),
  max_balance_hours numeric(8,2),
  waiting_days      int not null default 0,
  check (model = 'unpaid' or hours_per_grant > 0),
  check (year_boundary in ('calendar', 'anniversary'))
);

-- Append-only. Balance is the sum of hours.
-- unique (source, external_ref) allows many null external_ref values.
create table if not exists public.leave_ledger (
  id            uuid primary key default gen_random_uuid(),
  employee_id   uuid not null references public.employee (id),
  leave_type_id uuid not null references public.leave_type (id),
  kind          public.ledger_kind not null,
  hours         numeric(8,2) not null,
  effective_on  date not null,
  request_id    uuid,
  source        text not null,
  external_ref  text,
  created_at    timestamptz not null default now(),
  unique (source, external_ref)
);

create index if not exists leave_ledger_balance
  on public.leave_ledger (employee_id, leave_type_id, effective_on);

create table if not exists public.leave_request (
  id              uuid primary key default gen_random_uuid(),
  employee_id     uuid not null references public.employee (id),
  leave_type_id   uuid not null references public.leave_type (id),
  status          public.leave_status not null default 'pending',
  idempotency_key text not null,
  quote_id        uuid not null,
  admin_override  boolean not null default false,
  created_at      timestamptz not null default now(),
  decided_at      timestamptz,
  decided_by      uuid references public.employee (id),
  unique (employee_id, idempotency_key)
);

create index if not exists leave_request_queue
  on public.leave_request (employee_id, status);

-- employee_id and status are copied by trigger. The exclusion is the lock.
create table if not exists public.leave_request_day (
  request_id  uuid not null references public.leave_request (id),
  on_date     date not null,
  hours       numeric(8,2) not null check (hours > 0 and hours <= 24),
  employee_id uuid not null,
  status      public.leave_status not null,
  primary key (request_id, on_date)
);

do $fk$
begin
  if not exists (
    select 1 from pg_constraint
    where conname = 'leave_ledger_request_fk'
      and conrelid = 'public.leave_ledger'::regclass
  ) then
    alter table public.leave_ledger
      add constraint leave_ledger_request_fk
      foreign key (request_id) references public.leave_request (id);
  end if;
end
$fk$;

do $ex$
begin
  if not exists (
    select 1 from pg_constraint
    where conname = 'one_open_request_per_day'
      and conrelid = 'public.leave_request_day'::regclass
  ) then
    alter table public.leave_request_day
      add constraint one_open_request_per_day
      exclude using gist (
        employee_id with =,
        on_date with =
      ) where (status in ('pending', 'approved'));
  end if;
end
$ex$;

create table if not exists public.blackout_date (
  on_date date primary key,
  reason  text not null
);

create table if not exists public.action_quote (
  id            uuid primary key default gen_random_uuid(),
  employee_id   uuid not null references public.employee (id),
  kind          public.quote_kind not null,
  payload       jsonb not null,
  expires_at    timestamptz not null,
  consumed_at   timestamptz,
  confirmed_at  timestamptz,
  created_at    timestamptz not null default now()
);

do $fk$
begin
  if not exists (
    select 1 from pg_constraint
    where conname = 'leave_request_quote_fk'
      and conrelid = 'public.leave_request'::regclass
  ) then
    alter table public.leave_request
      add constraint leave_request_quote_fk
      foreign key (quote_id) references public.action_quote (id);
  end if;
end
$fk$;

-- IRS limits. Not constants in code.
-- HSA catch-up is its own row: kind hsa, coverage 'catch_up'.
create table if not exists public.deduction_cap (
  tax_year    int not null,
  kind        public.deduction_kind not null,
  coverage    text not null,
  limit_cents int not null check (limit_cents > 0),
  primary key (tax_year, kind, coverage)
);

create table if not exists public.tax_year_param (
  tax_year            int primary key,
  ss_wage_base_cents  int not null,
  ss_rate_bps         int not null,
  medicare_rate_bps   int not null,
  federal_brackets    jsonb not null
);

create table if not exists public.state_income_tax (
  tax_year            int not null,
  state_code          text not null,
  rate_bps            int not null,
  conforms_cafeteria  boolean not null,
  primary key (tax_year, state_code)
);

create table if not exists public.deduction_election (
  id                  uuid primary key default gen_random_uuid(),
  employee_id         uuid not null references public.employee (id),
  kind                public.deduction_kind not null,
  per_paycheck_cents  int not null check (per_paycheck_cents >= 0),
  status              public.election_status not null default 'pending_confirm',
  effective_on        date not null,
  ended_on            date,
  qualifying_event    text,
  quote_id            uuid not null references public.action_quote (id),
  idempotency_key     text not null,
  created_at          timestamptz not null default now(),
  unique (employee_id, idempotency_key)
);

create index if not exists deduction_election_open
  on public.deduction_election (employee_id, kind, status);

-- What payroll did. The app role cannot write this table.
create table if not exists public.payroll_posting (
  id             uuid primary key default gen_random_uuid(),
  employee_id    uuid not null references public.employee (id),
  pay_period_id  uuid not null references public.pay_period (id),
  kind           public.deduction_kind not null,
  amount_cents   int not null,
  external_ref   text not null unique,
  posted_at      timestamptz not null default now()
);

create table if not exists public.withholding_profile (
  employee_id    uuid primary key references public.employee (id),
  filing_status  text not null,
  w4             jsonb not null,
  state_code     text not null,
  updated_at     timestamptz not null default now()
);

create table if not exists public.webauthn_credential (
  id             uuid primary key default gen_random_uuid(),
  employee_id    uuid not null references public.employee (id),
  credential_id  bytea not null unique,
  public_key     bytea not null,
  sign_count     bigint not null default 0,
  aaguid         uuid,
  transports     text[] not null default '{}',
  nickname       text,
  created_at     timestamptz not null default now()
);

create table if not exists public.recovery_code (
  id           uuid primary key default gen_random_uuid(),
  employee_id  uuid not null references public.employee (id),
  code_hash    text not null,
  used_at      timestamptz
);

create table if not exists public.invite (
  id           uuid primary key default gen_random_uuid(),
  email        text not null,
  name         text not null,
  role         public.employee_role not null default 'employee',
  manager_id   uuid references public.employee (id),
  hired_on     date not null,
  jurisdiction text not null,
  timezone     text not null default 'America/New_York',
  token_hash   text not null unique,
  expires_at   timestamptz not null,
  consumed_at  timestamptz,
  created_by   uuid references public.employee (id),
  created_at   timestamptz not null default now()
);

create table if not exists public.app_session (
  id           uuid primary key,
  employee_id  uuid not null references public.employee (id),
  created_at   timestamptz not null default now(),
  expires_at   timestamptz not null,
  revoked_at   timestamptz
);

create index if not exists app_session_employee
  on public.app_session (employee_id);

create table if not exists public.webauthn_challenge (
  id           uuid primary key default gen_random_uuid(),
  employee_id  uuid references public.employee (id),
  invite_id    uuid references public.invite (id),
  kind         text not null,
  action       text,
  quote_id     uuid references public.action_quote (id),
  challenge    bytea not null,
  expires_at   timestamptz not null,
  consumed_at  timestamptz,
  check (kind in ('register', 'assert', 'step_up')),
  check (employee_id is not null or invite_id is not null)
);

create table if not exists public.mcp_client (
  id                    uuid primary key default gen_random_uuid(),
  employee_id           uuid not null references public.employee (id),
  client_name           text not null,
  oauth_application_id  text,
  revoked_at            timestamptz,
  created_at            timestamptz not null default now()
);

create or replace function public.copy_leave_request_day_parent()
returns trigger
language plpgsql
set search_path = public, pg_catalog, pg_temp
as $$
declare
  parent_employee uuid;
  parent_status public.leave_status;
begin
  select employee_id, status
    into parent_employee, parent_status
  from public.leave_request
  where id = new.request_id;

  if not found then
    raise exception 'leave_request % not found', new.request_id;
  end if;

  new.employee_id := parent_employee;
  new.status := parent_status;
  return new;
end
$$;

create or replace function public.sync_leave_request_status()
returns trigger
language plpgsql
set search_path = public, pg_catalog, pg_temp
as $$
begin
  if new.status is distinct from old.status then
    update public.leave_request_day
       set status = new.status
     where request_id = new.id;
  end if;
  return new;
end
$$;

create or replace function public.reject_append_only()
returns trigger
language plpgsql
set search_path = pg_catalog, pg_temp
as $$
begin
  raise exception '% is append-only', tg_table_name;
end
$$;

drop trigger if exists leave_request_day_copy_parent on public.leave_request_day;
create trigger leave_request_day_copy_parent
before insert or update on public.leave_request_day
for each row execute function public.copy_leave_request_day_parent();

drop trigger if exists leave_request_sync_day_status on public.leave_request;
create trigger leave_request_sync_day_status
after update of status on public.leave_request
for each row execute function public.sync_leave_request_status();

drop trigger if exists leave_ledger_append_only on public.leave_ledger;
create trigger leave_ledger_append_only
before update or delete on public.leave_ledger
for each row execute function public.reject_append_only();

drop trigger if exists payroll_posting_append_only on public.payroll_posting;
create trigger payroll_posting_append_only
before update or delete on public.payroll_posting
for each row execute function public.reject_append_only();
