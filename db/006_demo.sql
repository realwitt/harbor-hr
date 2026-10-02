-- Demo people and leave for the manager view.
-- Skip the test database. The statements are safe to run again.
-- Actor channel is system. An empty actor id is null.

select set_config('audit.channel', 'system', true);
select set_config('audit.auth_factor', 'system', true);
select set_config('audit.actor_id', '', true);
select set_config('audit.client_id', '', true);
select set_config('audit.request_id', '', true);
select set_config('audit.quote_id', '', true);
select set_config('audit.idempotency_key', '', true);

do $demo$
begin
  if current_database() = 'harbor_test' then
    return;
  end if;

  insert into public.employee (
    id, email, name, role, timezone, jurisdiction,
    hired_on, born_on, hdhp_eligible, hsa_coverage
  ) values
    (
      'a3333333-3333-4333-8333-333333333333',
      'jordan.hale@example.com',
      'Jordan Hale',
      'employee',
      'America/New_York',
      'US-NC',
      date '2024-06-03',
      date '1991-02-14',
      true,
      'family'
    ),
    (
      'a4444444-4444-4444-8444-444444444444',
      'priya.shah@example.com',
      'Priya Shah',
      'employee',
      'America/New_York',
      'US-NC',
      date '2025-01-13',
      date '1996-07-22',
      false,
      null
    ),
    (
      'a5555555-5555-4555-8555-555555555555',
      'chris.nguyen@example.com',
      'Chris Nguyen',
      'employee',
      'America/New_York',
      'US-NC',
      date '2023-09-18',
      date '1988-12-01',
      true,
      'self'
    ),
    (
      'a6666666-6666-4666-8666-666666666666',
      'avery.brooks@example.com',
      'Avery Brooks',
      'employee',
      'America/New_York',
      'US-NC',
      date '2026-02-02',
      date '1998-05-19',
      false,
      null
    ),
    (
      'a7777777-7777-4777-8777-777777777777',
      'morgan.lee@example.com',
      'Morgan Lee',
      'employee',
      'America/Chicago',
      'US-NC',
      date '2025-08-11',
      date '1993-09-30',
      false,
      null
    )
  on conflict (id) do update set
    email = excluded.email,
    name = excluded.name,
    role = excluded.role,
    timezone = excluded.timezone,
    jurisdiction = excluded.jurisdiction,
    hired_on = excluded.hired_on,
    born_on = excluded.born_on,
    hdhp_eligible = excluded.hdhp_eligible,
    hsa_coverage = excluded.hsa_coverage;

  -- Jordan, Priya, Chris, and Avery report to Elias.
  -- Morgan reports to Sam, so the Team page does not show Morgan.
  insert into public.manager_link (employee_id, manager_id, effective_on, ended_on)
  values
    ('a3333333-3333-4333-8333-333333333333', 'a1111111-1111-4111-8111-111111111111', date '2024-06-03', null),
    ('a4444444-4444-4444-8444-444444444444', 'a1111111-1111-4111-8111-111111111111', date '2025-01-13', null),
    ('a5555555-5555-4555-8555-555555555555', 'a1111111-1111-4111-8111-111111111111', date '2023-09-18', null),
    ('a6666666-6666-4666-8666-666666666666', 'a1111111-1111-4111-8111-111111111111', date '2026-02-02', null),
    ('a7777777-7777-4777-8777-777777777777', 'a2222222-2222-4222-8222-222222222222', date '2025-08-11', null)
  on conflict (employee_id, effective_on) do update set
    manager_id = excluded.manager_id,
    ended_on = excluded.ended_on;

  insert into public.action_quote (
    id, employee_id, kind, payload, expires_at, consumed_at, confirmed_at
  ) values
    ('c3010000-0000-4000-8000-000000000001', 'a2222222-2222-4222-8222-222222222222', 'leave', '{"action":"submit"}'::jsonb, timestamptz '2026-09-28 16:00:00+00', timestamptz '2026-09-28 15:00:00+00', timestamptz '2026-09-28 15:00:00+00'),
    ('c3010000-0000-4000-8000-000000000002', 'a3333333-3333-4333-8333-333333333333', 'leave', '{"action":"submit"}'::jsonb, timestamptz '2026-09-25 16:00:00+00', timestamptz '2026-09-25 15:00:00+00', timestamptz '2026-09-25 15:00:00+00'),
    ('c3010000-0000-4000-8000-000000000003', 'a1111111-1111-4111-8111-111111111111', 'leave', '{"action":"submit"}'::jsonb, timestamptz '2026-09-26 16:00:00+00', timestamptz '2026-09-26 15:00:00+00', timestamptz '2026-09-26 15:00:00+00'),
    ('c3010000-0000-4000-8000-000000000004', 'a4444444-4444-4444-8444-444444444444', 'leave', '{"action":"submit"}'::jsonb, timestamptz '2026-09-29 16:00:00+00', timestamptz '2026-09-29 15:00:00+00', timestamptz '2026-09-29 15:00:00+00'),
    ('c3010000-0000-4000-8000-000000000005', 'a5555555-5555-4555-8555-555555555555', 'leave', '{"action":"submit"}'::jsonb, timestamptz '2026-09-24 16:00:00+00', timestamptz '2026-09-24 15:00:00+00', timestamptz '2026-09-24 15:00:00+00'),
    ('c3010000-0000-4000-8000-000000000006', 'a6666666-6666-4666-8666-666666666666', 'leave', '{"action":"submit"}'::jsonb, timestamptz '2026-09-30 16:00:00+00', timestamptz '2026-09-30 15:00:00+00', timestamptz '2026-09-30 15:00:00+00'),
    ('c3010000-0000-4000-8000-000000000007', 'a7777777-7777-4777-8777-777777777777', 'leave', '{"action":"submit"}'::jsonb, timestamptz '2026-09-27 16:00:00+00', timestamptz '2026-09-27 15:00:00+00', timestamptz '2026-09-27 15:00:00+00')
  on conflict (id) do nothing;

  insert into public.leave_request (
    id, employee_id, leave_type_id, status, idempotency_key, quote_id,
    admin_override, created_at, decided_at, decided_by
  ) values
    (
      'd3010000-0000-4000-8000-000000000001',
      'a2222222-2222-4222-8222-222222222222',
      'b1111111-1111-4111-8111-111111111111',
      'pending',
      'demo-sam-pto-oct',
      'c3010000-0000-4000-8000-000000000001',
      false,
      timestamptz '2026-09-28 15:00:00+00',
      null,
      null
    ),
    (
      'd3010000-0000-4000-8000-000000000002',
      'a3333333-3333-4333-8333-333333333333',
      'b1111111-1111-4111-8111-111111111111',
      'approved',
      'demo-jordan-pto-oct',
      'c3010000-0000-4000-8000-000000000002',
      false,
      timestamptz '2026-09-25 15:00:00+00',
      timestamptz '2026-09-25 18:00:00+00',
      'a1111111-1111-4111-8111-111111111111'
    ),
    (
      'd3010000-0000-4000-8000-000000000003',
      'a1111111-1111-4111-8111-111111111111',
      'b1111111-1111-4111-8111-111111111111',
      'approved',
      'demo-elias-pto-oct',
      'c3010000-0000-4000-8000-000000000003',
      false,
      timestamptz '2026-09-26 15:00:00+00',
      timestamptz '2026-09-26 18:00:00+00',
      'a1111111-1111-4111-8111-111111111111'
    ),
    (
      'd3010000-0000-4000-8000-000000000004',
      'a4444444-4444-4444-8444-444444444444',
      'b2222222-2222-4222-8222-222222222222',
      'pending',
      'demo-priya-flex-oct',
      'c3010000-0000-4000-8000-000000000004',
      false,
      timestamptz '2026-09-29 15:00:00+00',
      null,
      null
    ),
    (
      'd3010000-0000-4000-8000-000000000005',
      'a5555555-5555-4555-8555-555555555555',
      'b2222222-2222-4222-8222-222222222222',
      'approved',
      'demo-chris-flex-oct',
      'c3010000-0000-4000-8000-000000000005',
      false,
      timestamptz '2026-09-24 15:00:00+00',
      timestamptz '2026-09-24 18:00:00+00',
      'a1111111-1111-4111-8111-111111111111'
    ),
    (
      'd3010000-0000-4000-8000-000000000006',
      'a6666666-6666-4666-8666-666666666666',
      'b3333333-3333-4333-8333-333333333333',
      'pending',
      'demo-avery-unpaid-oct',
      'c3010000-0000-4000-8000-000000000006',
      false,
      timestamptz '2026-09-30 15:00:00+00',
      null,
      null
    ),
    (
      'd3010000-0000-4000-8000-000000000007',
      'a7777777-7777-4777-8777-777777777777',
      'b1111111-1111-4111-8111-111111111111',
      'pending',
      'demo-morgan-pto-oct',
      'c3010000-0000-4000-8000-000000000007',
      false,
      timestamptz '2026-09-27 15:00:00+00',
      null,
      null
    )
  on conflict (employee_id, idempotency_key) do nothing;

  -- Skip a day that already has open leave. The exclusion constraint is not an ON CONFLICT target.
  insert into public.leave_request_day (request_id, on_date, hours, employee_id, status)
  select
    v.request_id,
    v.on_date,
    v.hours,
    v.employee_id,
    v.status
  from (
    values
      ('d3010000-0000-4000-8000-000000000001'::uuid, date '2026-10-06', 8::numeric, 'a2222222-2222-4222-8222-222222222222'::uuid, 'pending'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000001'::uuid, date '2026-10-07', 8::numeric, 'a2222222-2222-4222-8222-222222222222'::uuid, 'pending'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000002'::uuid, date '2026-10-02', 8::numeric, 'a3333333-3333-4333-8333-333333333333'::uuid, 'approved'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000003'::uuid, date '2026-10-08', 8::numeric, 'a1111111-1111-4111-8111-111111111111'::uuid, 'approved'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000004'::uuid, date '2026-10-09', 8::numeric, 'a4444444-4444-4444-8444-444444444444'::uuid, 'pending'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000005'::uuid, date '2026-10-13', 8::numeric, 'a5555555-5555-4555-8555-555555555555'::uuid, 'approved'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000005'::uuid, date '2026-10-14', 8::numeric, 'a5555555-5555-4555-8555-555555555555'::uuid, 'approved'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000006'::uuid, date '2026-10-15', 8::numeric, 'a6666666-6666-4666-8666-666666666666'::uuid, 'pending'::public.leave_status),
      ('d3010000-0000-4000-8000-000000000007'::uuid, date '2026-10-10', 8::numeric, 'a7777777-7777-4777-8777-777777777777'::uuid, 'pending'::public.leave_status)
  ) as v(request_id, on_date, hours, employee_id, status)
  where not exists (
    select 1
    from public.leave_request_day as open_day
    where open_day.employee_id = v.employee_id
      and open_day.on_date = v.on_date
      and open_day.status in ('pending', 'approved')
  )
  on conflict (request_id, on_date) do nothing;

  -- Approved days already reduce the balance. Pending days do not.
  insert into public.leave_ledger (
    employee_id, leave_type_id, kind, hours, effective_on, request_id, source, external_ref
  )
  select
    v.employee_id,
    v.leave_type_id,
    'usage',
    -8,
    v.on_date,
    v.request_id,
    'seed',
    v.external_ref
  from (
    values
      ('a3333333-3333-4333-8333-333333333333'::uuid, 'b1111111-1111-4111-8111-111111111111'::uuid, date '2026-10-02', 'd3010000-0000-4000-8000-000000000002'::uuid, 'demo-usage:jordan:2026-10-02'),
      ('a1111111-1111-4111-8111-111111111111'::uuid, 'b1111111-1111-4111-8111-111111111111'::uuid, date '2026-10-08', 'd3010000-0000-4000-8000-000000000003'::uuid, 'demo-usage:elias:2026-10-08'),
      ('a5555555-5555-4555-8555-555555555555'::uuid, 'b2222222-2222-4222-8222-222222222222'::uuid, date '2026-10-13', 'd3010000-0000-4000-8000-000000000005'::uuid, 'demo-usage:chris:2026-10-13'),
      ('a5555555-5555-4555-8555-555555555555'::uuid, 'b2222222-2222-4222-8222-222222222222'::uuid, date '2026-10-14', 'd3010000-0000-4000-8000-000000000005'::uuid, 'demo-usage:chris:2026-10-14')
  ) as v(employee_id, leave_type_id, on_date, request_id, external_ref)
  where exists (
    select 1
    from public.leave_request_day as day
    where day.request_id = v.request_id
      and day.on_date = v.on_date
  )
  on conflict (source, external_ref) do nothing;
end
$demo$;
