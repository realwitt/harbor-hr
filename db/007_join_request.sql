-- A person asks to join. An HR admin approves by creating an invite.
-- One pending request per email. The invite token is not stored here.

create table if not exists public.join_request (
  id          uuid primary key default gen_random_uuid(),
  email       text not null,
  name        text not null,
  note        text,
  status      text not null default 'pending',
  created_at  timestamptz not null default now(),
  decided_at  timestamptz,
  decided_by  uuid references public.employee (id),
  invite_id   uuid references public.invite (id),
  check (status in ('pending', 'approved', 'dismissed')),
  check (note is null or char_length(note) <= 500)
);

create unique index if not exists join_request_one_pending_email
  on public.join_request (email)
  where status = 'pending';

grant select, insert, update, delete on public.join_request to harbor_app;

select audit.enable_tracking('public.join_request'::regclass);
