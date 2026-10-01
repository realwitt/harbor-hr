-- Idempotent seed. Fixed employee and leave-type ids. Safe to run twice.
-- Actor channel is system. An empty actor id is null.

select set_config('audit.channel', 'system', true);
select set_config('audit.auth_factor', 'system', true);
select set_config('audit.actor_id', '', true);
select set_config('audit.client_id', '', true);
select set_config('audit.request_id', '', true);
select set_config('audit.quote_id', '', true);
select set_config('audit.idempotency_key', '', true);

-- Elias Witt, hr_admin. Sam Reyes reports to Elias.
insert into public.employee (
  id, email, name, role, timezone, jurisdiction,
  hired_on, born_on, hdhp_eligible, hsa_coverage
) values (
  'a1111111-1111-4111-8111-111111111111',
  'ew@eliaswitt.com',
  'Elias Witt',
  'hr_admin',
  'America/New_York',
  'US-NC',
  date '2024-01-15',
  date '1990-04-02',
  true,
  'self'
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

insert into public.employee (
  id, email, name, role, timezone, jurisdiction,
  hired_on, born_on, hdhp_eligible, hsa_coverage
) values (
  'a2222222-2222-4222-8222-222222222222',
  'sam.reyes@example.com',
  'Sam Reyes',
  'employee',
  'America/New_York',
  'US-NC',
  date '2025-03-03',
  date '1994-11-11',
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

insert into public.manager_link (employee_id, manager_id, effective_on, ended_on)
values (
  'a2222222-2222-4222-8222-222222222222',
  'a1111111-1111-4111-8111-111111111111',
  date '2025-03-03',
  null
)
on conflict (employee_id, effective_on) do update set
  manager_id = excluded.manager_id,
  ended_on = excluded.ended_on;

insert into public.leave_type (
  id, code, model, hours_per_grant, year_boundary,
  carry_cap_hours, max_balance_hours, waiting_days
) values
  (
    'b1111111-1111-4111-8111-111111111111',
    'pto', 'accrued', 3.08, 'calendar', 40, 120, 0
  ),
  (
    'b2222222-2222-4222-8222-222222222222',
    'flex', 'instant', 40, 'calendar', null, null, 0
  ),
  (
    'b3333333-3333-4333-8333-333333333333',
    'unpaid', 'unpaid', null, 'calendar', null, null, 0
  )
on conflict (id) do update set
  code = excluded.code,
  model = excluded.model,
  hours_per_grant = excluded.hours_per_grant,
  year_boundary = excluded.year_boundary,
  carry_cap_hours = excluded.carry_cap_hours,
  max_balance_hours = excluded.max_balance_hours,
  waiting_days = excluded.waiting_days;

-- Biweekly periods that overlap 2026.
-- First period: 2025-12-21 through 2026-01-03, pay date 2026-01-09.
-- Then every 14 days. The last period still covers 2026-12-31. Ranges do not overlap.
insert into public.pay_period (id, starts_on, ends_on, pay_date)
select
  (
    substr(h, 1, 8) || '-' ||
    substr(h, 9, 4) || '-' ||
    '4' || substr(h, 14, 3) || '-' ||
    '8' || substr(h, 18, 3) || '-' ||
    substr(h, 21, 12)
  )::uuid,
  starts_on,
  ends_on,
  pay_date
from (
  select
    (date '2025-12-21' + (n * 14)) as starts_on,
    (date '2026-01-03' + (n * 14)) as ends_on,
    (date '2026-01-09' + (n * 14)) as pay_date,
    md5('harbor-pay-period:' || (date '2025-12-21' + (n * 14))::text) as h
  from generate_series(0, 40) as n
) periods
where starts_on <= date '2026-12-31'
  and ends_on >= date '2026-01-01'
on conflict (starts_on, ends_on) do nothing;

insert into public.company_holiday (on_date, name) values
  (date '2026-01-01', 'New Year''s Day'),
  (date '2026-01-19', 'Martin Luther King Jr. Day'),
  (date '2026-05-25', 'Memorial Day'),
  (date '2026-07-03', 'Independence Day'),
  (date '2026-09-07', 'Labor Day'),
  (date '2026-11-26', 'Thanksgiving Day'),
  (date '2026-12-25', 'Christmas Day')
on conflict (on_date) do update set name = excluded.name;

insert into public.blackout_date (on_date, reason) values
  (date '2026-12-24', 'Christmas Eve')
on conflict (on_date) do update set reason = excluded.reason;

-- 2026 HSA: Rev. Proc. 2025-19 / PRD. self $4,400, family $8,750, catch-up $1,000.
-- 2026 health FSA: $3,400, Rev. Proc. 2025-32.
-- 2026 dependent care FSA: $7,500, OBBBA / Rev. Proc. 2025-32 reports.
-- 2027 HSA: PRD, Rev. Proc. 2026-24. self $4,500, family $9,000, catch-up $1,000.
-- No 2027 FSA caps in this seed.
insert into public.deduction_cap (tax_year, kind, coverage, limit_cents) values
  (2026, 'hsa', 'self', 440000),
  (2026, 'hsa', 'family', 875000),
  (2026, 'hsa', 'catch_up', 100000),
  (2026, 'health_fsa', 'employee', 340000),
  (2026, 'dependent_care_fsa', 'employee', 750000),
  (2027, 'hsa', 'self', 450000),
  (2027, 'hsa', 'family', 900000),
  (2027, 'hsa', 'catch_up', 100000)
on conflict (tax_year, kind, coverage) do update set
  limit_cents = excluded.limit_cents;

-- 2026 federal brackets and standard deduction: IRS Rev. Proc. 2025-32.
-- Amounts are cents. rate_bps is the marginal rate. Null up_to_cents is the top bracket.
-- Social Security wage base $184,500. Employee rates: OASDI 6.20%, Medicare 1.45%.
-- No 2027 Social Security wage base in this seed.
insert into public.tax_year_param (
  tax_year, ss_wage_base_cents, ss_rate_bps, medicare_rate_bps, federal_brackets
) values (
  2026,
  18450000,
  620,
  145,
  jsonb_build_object(
    'standard_deduction_cents', jsonb_build_object(
      'single', 1610000,
      'married_joint', 3220000,
      'head', 2415000
    ),
    'single', jsonb_build_array(
      jsonb_build_object('up_to_cents', 1240000, 'rate_bps', 1000),
      jsonb_build_object('up_to_cents', 5040000, 'rate_bps', 1200),
      jsonb_build_object('up_to_cents', 10570000, 'rate_bps', 2200),
      jsonb_build_object('up_to_cents', 20177500, 'rate_bps', 2400),
      jsonb_build_object('up_to_cents', 25622500, 'rate_bps', 3200),
      jsonb_build_object('up_to_cents', 64060000, 'rate_bps', 3500),
      jsonb_build_object('up_to_cents', null, 'rate_bps', 3700)
    ),
    'married_joint', jsonb_build_array(
      jsonb_build_object('up_to_cents', 2480000, 'rate_bps', 1000),
      jsonb_build_object('up_to_cents', 10080000, 'rate_bps', 1200),
      jsonb_build_object('up_to_cents', 21140000, 'rate_bps', 2200),
      jsonb_build_object('up_to_cents', 40355000, 'rate_bps', 2400),
      jsonb_build_object('up_to_cents', 51245000, 'rate_bps', 3200),
      jsonb_build_object('up_to_cents', 76870000, 'rate_bps', 3500),
      jsonb_build_object('up_to_cents', null, 'rate_bps', 3700)
    ),
    'head', jsonb_build_array(
      jsonb_build_object('up_to_cents', 1770000, 'rate_bps', 1000),
      jsonb_build_object('up_to_cents', 6745000, 'rate_bps', 1200),
      jsonb_build_object('up_to_cents', 10570000, 'rate_bps', 2200),
      jsonb_build_object('up_to_cents', 20177500, 'rate_bps', 2400),
      jsonb_build_object('up_to_cents', 25620000, 'rate_bps', 3200),
      jsonb_build_object('up_to_cents', 64060000, 'rate_bps', 3500),
      jsonb_build_object('up_to_cents', null, 'rate_bps', 3700)
    )
  )
)
on conflict (tax_year) do update set
  ss_wage_base_cents = excluded.ss_wage_base_cents,
  ss_rate_bps = excluded.ss_rate_bps,
  medicare_rate_bps = excluded.medicare_rate_bps,
  federal_brackets = excluded.federal_brackets;

-- 2026 NC: NCDOR 3.99%. 2027 NC: NC OSBM, 3.49% beginning tax year 2027.
insert into public.state_income_tax (tax_year, state_code, rate_bps, conforms_cafeteria) values
  (2026, 'NC', 399, true),
  (2027, 'NC', 349, true)
on conflict (tax_year, state_code) do update set
  rate_bps = excluded.rate_bps,
  conforms_cafeteria = excluded.conforms_cafeteria;

insert into public.withholding_profile (employee_id, filing_status, state_code, w4) values
  (
    'a1111111-1111-4111-8111-111111111111',
    'single',
    'NC',
    jsonb_build_object(
      'other_income_cents', 0,
      'deductions_cents', 0,
      'extra_withholding_cents', 0,
      'dependents_credit_cents', 0,
      'gross_per_period_cents', 400000,
      'ytd_wages_cents', 7200000
    )
  ),
  (
    'a2222222-2222-4222-8222-222222222222',
    'single',
    'NC',
    jsonb_build_object(
      'other_income_cents', 0,
      'deductions_cents', 0,
      'extra_withholding_cents', 0,
      'dependents_credit_cents', 0,
      'gross_per_period_cents', 280000,
      'ytd_wages_cents', 4200000
    )
  )
on conflict (employee_id) do update set
  filing_status = excluded.filing_status,
  state_code = excluded.state_code,
  w4 = excluded.w4;

-- One PTO accrual per employee for each 2026 pay date on or before 2026-10-01.
insert into public.leave_ledger (
  employee_id, leave_type_id, kind, hours, effective_on, source, external_ref
)
select
  e.id,
  lt.id,
  'accrual',
  3.08,
  p.pay_date,
  'schedule',
  'accrual:' || e.id::text || ':' || lt.id::text || ':' || p.pay_date::text
from public.employee e
cross join public.leave_type lt
join public.pay_period p on p.pay_date <= date '2026-10-01'
where lt.code = 'pto'
  and e.id in (
    'a1111111-1111-4111-8111-111111111111',
    'a2222222-2222-4222-8222-222222222222'
  )
on conflict (source, external_ref) do nothing;

-- One flex grant per employee for 2026.
insert into public.leave_ledger (
  employee_id, leave_type_id, kind, hours, effective_on, source, external_ref
)
select
  e.id,
  lt.id,
  'grant',
  40,
  date '2026-01-01',
  'schedule',
  'grant:' || e.id::text || ':' || lt.id::text || ':2026'
from public.employee e
cross join public.leave_type lt
where lt.code = 'flex'
  and e.id in (
    'a1111111-1111-4111-8111-111111111111',
    'a2222222-2222-4222-8222-222222222222'
  )
on conflict (source, external_ref) do nothing;

do $check$
declare
  period_count int;
  accrual_count int;
  expected_accruals int;
begin
  if (select count(*) from public.employee) <> 2 then
    raise exception 'seed employee count';
  end if;
  if (select count(*) from public.manager_link where ended_on is null) <> 1 then
    raise exception 'seed manager link count';
  end if;
  if (select count(*) from public.leave_type) <> 3 then
    raise exception 'seed leave type count';
  end if;
  if (select count(*) from public.company_holiday) <> 7 then
    raise exception 'seed holiday count';
  end if;
  if (select count(*) from public.blackout_date) <> 1 then
    raise exception 'seed blackout count';
  end if;
  if (select count(*) from public.deduction_cap) <> 8 then
    raise exception 'seed deduction cap count';
  end if;
  if (select count(*) from public.tax_year_param) <> 1 then
    raise exception 'seed tax year count';
  end if;
  if (select count(*) from public.state_income_tax) <> 2 then
    raise exception 'seed state tax count';
  end if;
  if (select count(*) from public.withholding_profile) <> 2 then
    raise exception 'seed withholding count';
  end if;

  select count(*) into period_count from public.pay_period;
  if (select min(starts_on) from public.pay_period) <> date '2025-12-21' then
    raise exception 'seed first pay period';
  end if;
  if (select min(ends_on) from public.pay_period) <> date '2026-01-03' then
    raise exception 'seed first pay period end';
  end if;
  if (select min(pay_date) from public.pay_period) <> date '2026-01-09' then
    raise exception 'seed first pay date';
  end if;
  if (select max(ends_on) from public.pay_period) < date '2026-12-31' then
    raise exception 'seed pay periods stop before the end of 2026';
  end if;
  if (select max(ends_on) from public.pay_period) > date '2027-01-16' then
    raise exception 'seed pay periods run past January 2027';
  end if;
  if period_count < 26 then
    raise exception 'seed pay period count %', period_count;
  end if;

  select count(*) into expected_accruals
  from public.employee e
  cross join public.pay_period p
  where e.id in (
      'a1111111-1111-4111-8111-111111111111',
      'a2222222-2222-4222-8222-222222222222'
    )
    and p.pay_date <= date '2026-10-01';

  select count(*) into accrual_count
  from public.leave_ledger
  where kind = 'accrual' and source = 'schedule';

  if accrual_count <> expected_accruals then
    raise exception 'seed accrual count % expected %', accrual_count, expected_accruals;
  end if;

  if (select count(*) from public.leave_ledger where kind = 'grant' and source = 'schedule') <> 2 then
    raise exception 'seed flex grant count';
  end if;
end
$check$;
