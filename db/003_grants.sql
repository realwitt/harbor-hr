-- harbor_app grants. The owner role harbor runs migrations and is not granted here.
-- leave_ledger is INSERT + SELECT. payroll_posting is SELECT.
-- Audit tables are SELECT. Inserts go through security definer functions.
-- No UPDATE or DELETE on the ledger, postings, or audit tables.

grant usage on schema public to harbor_app;
grant usage on schema audit to harbor_app;

revoke all privileges on all tables in schema public from harbor_app;
grant select, insert, update, delete on all tables in schema public to harbor_app;
revoke insert, update, delete on table public.payroll_posting from harbor_app;
revoke update, delete on table public.leave_ledger from harbor_app;

revoke all privileges on all tables in schema audit from harbor_app;
grant select on table audit.record_version, audit.access_event to harbor_app;
revoke all privileges on all sequences in schema audit from harbor_app;

revoke all on function audit.record_change() from public;
grant execute on function audit.record_change() to harbor_app;

revoke all on function audit.record_access(text, text, uuid, text, jsonb) from public;
grant execute on function audit.record_access(text, text, uuid, text, jsonb) to harbor_app;

revoke all on function audit.enable_tracking(regclass) from public;
revoke all on function audit.redact_document(jsonb) from public;
revoke all on function audit.mask_session_id(jsonb) from public;
revoke all on function audit.record_identity(name, name, oid, jsonb) from public;
revoke all on function audit.read_context() from public;
revoke all on function audit.parse_uuid(text) from public;
revoke all on function audit.parse_channel(text) from public;
revoke all on function audit.parse_auth_factor(text) from public;
revoke all on function audit.reject_change() from public;

grant execute on function public.copy_leave_request_day_parent() to harbor_app;
grant execute on function public.sync_leave_request_status() to harbor_app;
grant execute on function public.reject_append_only() to harbor_app;
