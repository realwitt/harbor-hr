import { useForm } from '@tanstack/react-form'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../api'
import { isGuid, kindLabel, money, parseHours, statusLabel } from '../format'
import type { AuditLog, Blackout, DeductionCap, Holiday, LeaveType, PayPeriod } from '../types'
import { Button, Check, Choice, DateField, ErrorText, Page, TextField, fieldErrors } from '../ui'
import { stepUp } from '../webauthn'

const models = [
  { id: 'accrued', label: 'Accrued' },
  { id: 'instant', label: 'Instant' },
  { id: 'unpaid', label: 'Unpaid' },
]
const boundaries = [
  { id: 'calendar', label: 'Calendar' },
  { id: 'anniversary', label: 'Anniversary' },
]
const roles = [
  { id: 'employee', label: 'Employee' },
  { id: 'hr_admin', label: 'HR admin' },
]
const kinds = [
  { id: 'hsa', label: 'HSA' },
  { id: 'health_fsa', label: 'Health FSA' },
  { id: 'dependent_care_fsa', label: 'Dependent care FSA' },
]
const channels = [
  { id: 'any', label: 'Any channel' },
  { id: 'web', label: 'Web' },
  { id: 'mcp', label: 'MCP' },
  { id: 'job', label: 'Job' },
  { id: 'system', label: 'System' },
]
const coverageChoices = [
  { id: 'none', label: 'None' },
  { id: 'self', label: 'Self' },
  { id: 'family', label: 'Family' },
]

function optionalNumber(text: string): number | null {
  const trimmed = text.trim()
  if (!trimmed) {
    return null
  }

  return parseHours(trimmed, false)
}

export function AdminPage() {
  return (
    <Page title="Admin">
      <LeaveTypes />
      <Blackouts />
      <Caps />
      <Invites />
      <Hdhp />
      <Ledger />
      <PayPeriods />
      <Holidays />
      <Audit />
    </Page>
  )
}

function LeaveTypes() {
  const queryClient = useQueryClient()
  const types = useQuery({
    queryKey: ['admin-leave-types'],
    queryFn: () => api<LeaveType[]>('/api/admin/leave-types'),
  })
  const form = useForm({
    defaultValues: {
      code: '',
      model: 'accrued',
      hoursPerGrant: '',
      yearBoundary: 'calendar',
      carryCapHours: '',
      maxBalanceHours: '',
      waitingDays: '0',
    },
    onSubmit: async ({ value }) => {
      const hours = value.model === 'unpaid' ? null : parseHours(value.hoursPerGrant, false)
      if (value.model !== 'unpaid' && (hours === null || hours <= 0)) {
        throw new Error('Hours per grant must be greater than 0.')
      }

      const carry = optionalNumber(value.carryCapHours)
      const max = optionalNumber(value.maxBalanceHours)
      if (value.carryCapHours.trim() && carry === null) {
        throw new Error('The carry cap is not valid.')
      }

      if (value.maxBalanceHours.trim() && max === null) {
        throw new Error('The max balance is not valid.')
      }

      const waiting = Number(value.waitingDays)
      if (!Number.isInteger(waiting) || waiting < 0) {
        throw new Error('Waiting days must be zero or greater.')
      }

      await api('/api/admin/leave-types', {
        method: 'POST',
        body: JSON.stringify({
          code: value.code.trim(),
          model: value.model,
          hoursPerGrant: hours,
          yearBoundary: value.yearBoundary,
          carryCapHours: carry,
          maxBalanceHours: max,
          waitingDays: waiting,
        }),
      })
      await queryClient.invalidateQueries({ queryKey: ['admin-leave-types'] })
      await queryClient.invalidateQueries({ queryKey: ['leave-types'] })
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">Leave types</h2>
      <ErrorText error={types.error} />
      {types.data && types.data.length === 0 ? <p className="text-sm">No leave type.</p> : null}
      {types.data?.map((type) => (
        <p key={type.id} className="text-sm">
          {type.code} · {type.model} · {type.hoursPerGrant ?? 'no grant'} h · {type.yearBoundary}
        </p>
      ))}
      <FormShell form={form}>
        <form.Field name="code" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The code is required.') }}>
          {(field) => (
            <TextField label="Code" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
        <form.Field name="model">
          {(field) => <Choice label="Model" value={field.state.value} onChange={field.handleChange} options={models} />}
        </form.Field>
        <form.Subscribe selector={(state) => state.values.model}>
          {(model) =>
            model === 'unpaid' ? null : (
              <form.Field name="hoursPerGrant">
                {(field) => (
                  <TextField label="Hours per grant" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />
                )}
              </form.Field>
            )
          }
        </form.Subscribe>
        <form.Field name="yearBoundary">
          {(field) => <Choice label="Year boundary" value={field.state.value} onChange={field.handleChange} options={boundaries} />}
        </form.Field>
        <form.Field name="carryCapHours">
          {(field) => <TextField label="Carry cap hours" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <form.Field name="maxBalanceHours">
          {(field) => <TextField label="Max balance hours" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <form.Field name="waitingDays">
          {(field) => <TextField label="Waiting days" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
      </FormShell>
    </section>
  )
}

function Blackouts() {
  const queryClient = useQueryClient()
  const rows = useQuery({
    queryKey: ['blackouts'],
    queryFn: () => api<Blackout[]>('/api/admin/blackouts'),
  })
  const remove = useMutation({
    mutationFn: (on: string) => api(`/api/admin/blackouts/${encodeURIComponent(on)}`, { method: 'DELETE' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['blackouts'] })
    },
  })
  const form = useForm({
    defaultValues: { on: '', reason: '' },
    onSubmit: async ({ value }) => {
      await api('/api/admin/blackouts', {
        method: 'POST',
        body: JSON.stringify({ on: value.on, reason: value.reason.trim() }),
      })
      await queryClient.invalidateQueries({ queryKey: ['blackouts'] })
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">Blackouts</h2>
      <ErrorText error={rows.error ?? remove.error} />
      {rows.data && rows.data.length === 0 ? <p className="text-sm">No blackout date.</p> : null}
      {rows.data?.map((row) => (
        <div key={row.on} className="flex items-center gap-2 text-sm">
          <span>
            {row.on} · {row.reason}
          </span>
          <Button quiet onPress={() => remove.mutate(row.on)} isDisabled={remove.isPending}>
            Remove
          </Button>
        </div>
      ))}
      <FormShell form={form} label="Add blackout">
        <form.Field name="on" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
          {(field) => <DateField label="On" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />}
        </form.Field>
        <form.Field name="reason" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The reason is required.') }}>
          {(field) => (
            <TextField label="Reason" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
      </FormShell>
    </section>
  )
}

function Caps() {
  const queryClient = useQueryClient()
  const caps = useQuery({
    queryKey: ['deduction-caps'],
    queryFn: () => api<DeductionCap[]>('/api/admin/deduction-caps'),
  })
  const form = useForm({
    defaultValues: { taxYear: String(new Date().getFullYear()), kind: 'hsa', coverage: '', limit: '' },
    onSubmit: async ({ value }) => {
      const year = Number(value.taxYear)
      const cents = Number(value.limit)
      if (!Number.isInteger(year)) {
        throw new Error('The tax year is not valid.')
      }

      if (!Number.isInteger(cents) || cents <= 0) {
        throw new Error('The limit must be greater than 0.')
      }

      await api('/api/admin/deduction-caps', {
        method: 'POST',
        body: JSON.stringify({
          taxYear: year,
          kind: value.kind,
          coverage: value.coverage.trim(),
          limitCents: cents,
        }),
      })
      await queryClient.invalidateQueries({ queryKey: ['deduction-caps'] })
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">Deduction caps</h2>
      <ErrorText error={caps.error} />
      {caps.data && caps.data.length === 0 ? <p className="text-sm">No deduction cap.</p> : null}
      {caps.data?.map((cap) => (
        <p key={`${cap.taxYear}-${cap.kind}-${cap.coverage}`} className="text-sm">
          {cap.taxYear} · {kindLabel(cap.kind)} · {cap.coverage} · {money(cap.limitCents)}
        </p>
      ))}
      <FormShell form={form} label="Add cap">
        <form.Field name="taxYear">
          {(field) => <TextField label="Tax year" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <form.Field name="kind">
          {(field) => <Choice label="Type" value={field.state.value} onChange={field.handleChange} options={kinds} />}
        </form.Field>
        <form.Field name="coverage" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The coverage is required.') }}>
          {(field) => (
            <TextField label="Coverage" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
        <form.Field name="limit">
          {(field) => <TextField label="Limit in cents" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
      </FormShell>
    </section>
  )
}

function Invites() {
  const [path, setPath] = useState('')
  const form = useForm({
    defaultValues: {
      email: '',
      name: '',
      role: 'employee',
      managerId: '',
      hiredOn: '',
      jurisdiction: '',
      timezone: 'America/New_York',
    },
    onSubmit: async ({ value }) => {
      if (value.managerId.trim() && !isGuid(value.managerId)) {
        throw new Error('The manager id is not valid.')
      }

      const result = await api<{ path: string }>('/api/auth/invites', {
        method: 'POST',
        body: JSON.stringify({
          email: value.email.trim(),
          name: value.name.trim(),
          role: value.role,
          managerId: value.managerId.trim() || null,
          hiredOn: value.hiredOn,
          jurisdiction: value.jurisdiction.trim(),
          timezone: value.timezone.trim() || 'America/New_York',
        }),
      })
      setPath(result.path)
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">Invites</h2>
      <FormShell form={form} label="Create invite">
        <form.Field name="email" validators={{ onSubmit: ({ value }) => (value.includes('@') ? undefined : 'Enter an email address.') }}>
          {(field) => (
            <TextField label="Email" type="email" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
        <form.Field name="name" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The name is required.') }}>
          {(field) => (
            <TextField label="Name" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
        <form.Field name="role">
          {(field) => <Choice label="Role" value={field.state.value} onChange={field.handleChange} options={roles} />}
        </form.Field>
        <form.Field name="managerId">
          {(field) => <TextField label="Manager id" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <form.Field name="hiredOn" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
          {(field) => <DateField label="Hired on" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />}
        </form.Field>
        <form.Field name="jurisdiction" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The jurisdiction is required.') }}>
          {(field) => (
            <TextField label="Jurisdiction" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
        <form.Field name="timezone">
          {(field) => <TextField label="Timezone" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
      </FormShell>
      {path ? <p className="text-sm">Invite path: {path}</p> : null}
    </section>
  )
}

function Hdhp() {
  const [eligible, setEligible] = useState(false)
  const [coverage, setCoverage] = useState('none')
  const form = useForm({
    defaultValues: { employeeId: '' },
    onSubmit: async ({ value }) => {
      if (!isGuid(value.employeeId)) {
        throw new Error('The employee id is not valid.')
      }

      await api(`/api/admin/employees/${value.employeeId.trim()}/hdhp`, {
        method: 'POST',
        body: JSON.stringify({
          hdhpEligible: eligible,
          hsaCoverage: eligible && coverage !== 'none' ? coverage : null,
        }),
      })
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">HDHP</h2>
      <FormShell form={form} label="Save HDHP">
        <form.Field name="employeeId">
          {(field) => <TextField label="Employee id" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <Check label="HDHP eligible" isSelected={eligible} onChange={setEligible} />
        <Choice label="HSA coverage" value={coverage} onChange={setCoverage} options={coverageChoices} />
      </FormShell>
    </section>
  )
}

function Ledger() {
  const types = useQuery({
    queryKey: ['admin-leave-types'],
    queryFn: () => api<LeaveType[]>('/api/admin/leave-types'),
  })
  const form = useForm({
    defaultValues: { employeeId: '', leaveTypeId: '', hours: '', effectiveOn: '', note: '' },
    onSubmit: async ({ value }) => {
      if (!isGuid(value.employeeId)) {
        throw new Error('The employee id is not valid.')
      }

      const hours = parseHours(value.hours, true)
      if (hours === null) {
        throw new Error('The hours are not valid.')
      }

      await api('/api/admin/ledger', {
        method: 'POST',
        body: JSON.stringify({
          employeeId: value.employeeId.trim(),
          leaveTypeId: value.leaveTypeId,
          hours,
          effectiveOn: value.effectiveOn,
          note: value.note.trim(),
        }),
      })
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">Ledger adjustment</h2>
      <ErrorText error={types.error} />
      <FormShell form={form} label="Add adjustment">
        <form.Field name="employeeId">
          {(field) => <TextField label="Employee id" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <form.Field name="leaveTypeId" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a leave type.') }}>
          {(field) => (
            <Choice
              label="Leave type"
              value={field.state.value}
              onChange={field.handleChange}
              error={fieldErrors(field.state.meta.errors)}
              options={(types.data ?? []).map((type) => ({ id: type.id, label: type.code }))}
            />
          )}
        </form.Field>
        <form.Field name="hours">
          {(field) => <TextField label="Hours" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <form.Field name="effectiveOn" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
          {(field) => <DateField label="Effective on" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />}
        </form.Field>
        <form.Field name="note" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The note is required.') }}>
          {(field) => (
            <TextField label="Note" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
      </FormShell>
    </section>
  )
}

function PayPeriods() {
  const queryClient = useQueryClient()
  const periods = useQuery({
    queryKey: ['pay-periods'],
    queryFn: () => api<PayPeriod[]>('/api/admin/pay-periods'),
  })
  const form = useForm({
    defaultValues: { startsOn: '', endsOn: '', payDate: '' },
    onSubmit: async ({ value }) => {
      await api('/api/admin/pay-periods', {
        method: 'POST',
        body: JSON.stringify(value),
      })
      await queryClient.invalidateQueries({ queryKey: ['pay-periods'] })
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">Pay periods</h2>
      <ErrorText error={periods.error} />
      {periods.data && periods.data.length === 0 ? <p className="text-sm">No pay period.</p> : null}
      {periods.data?.map((period) => (
        <p key={period.id} className="text-sm">
          {period.startsOn} – {period.endsOn} · pay {period.payDate}
        </p>
      ))}
      <FormShell form={form} label="Add pay period">
        <form.Field name="startsOn" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
          {(field) => <DateField label="Starts" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />}
        </form.Field>
        <form.Field name="endsOn" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
          {(field) => <DateField label="Ends" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />}
        </form.Field>
        <form.Field name="payDate" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
          {(field) => <DateField label="Pay date" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />}
        </form.Field>
      </FormShell>
    </section>
  )
}

function Holidays() {
  const queryClient = useQueryClient()
  const holidays = useQuery({
    queryKey: ['holidays'],
    queryFn: () => api<Holiday[]>('/api/admin/holidays'),
  })
  const form = useForm({
    defaultValues: { on: '', name: '' },
    onSubmit: async ({ value }) => {
      await api('/api/admin/holidays', {
        method: 'POST',
        body: JSON.stringify({ on: value.on, name: value.name.trim() }),
      })
      await queryClient.invalidateQueries({ queryKey: ['holidays'] })
    },
  })

  return (
    <section className="flex flex-col gap-2 border-b border-neutral-200 pb-3">
      <h2 className="text-sm font-semibold">Holidays</h2>
      <ErrorText error={holidays.error} />
      {holidays.data && holidays.data.length === 0 ? <p className="text-sm">No holiday.</p> : null}
      {holidays.data?.map((holiday) => (
        <p key={holiday.on} className="text-sm">
          {holiday.on} · {holiday.name}
        </p>
      ))}
      <FormShell form={form} label="Add holiday">
        <form.Field name="on" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
          {(field) => <DateField label="On" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />}
        </form.Field>
        <form.Field name="name" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The name is required.') }}>
          {(field) => (
            <TextField label="Name" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
      </FormShell>
    </section>
  )
}

function Audit() {
  const queryClient = useQueryClient()
  const [filters, setFilters] = useState({ actorId: '', table: '', channel: 'any', from: '', to: '' })
  const [armed, setArmed] = useState(false)
  const read = useMutation({
    mutationFn: () => stepUp('read_audit'),
    onSuccess: async () => {
      setArmed(true)
      await queryClient.invalidateQueries({ queryKey: ['audit'] })
    },
  })
  const audit = useQuery({
    queryKey: ['audit', filters],
    enabled: armed,
    queryFn: () => {
      const params = new URLSearchParams()
      if (filters.actorId.trim()) {
        params.set('actorId', filters.actorId.trim())
      }

      if (filters.table.trim()) {
        params.set('table', filters.table.trim())
      }

      if (filters.channel && filters.channel !== 'any') {
        params.set('channel', filters.channel)
      }

      if (filters.from) {
        params.set('from', filters.from)
      }

      if (filters.to) {
        params.set('to', filters.to)
      }

      const query = params.toString()
      return api<AuditLog>(`/api/admin/audit${query ? `?${query}` : ''}`)
    },
  })

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-semibold">Audit log</h2>
      <div className="flex flex-wrap gap-2">
        <TextField label="Actor id" value={filters.actorId} onChange={(actorId) => setFilters({ ...filters, actorId })} />
        <TextField label="Table" value={filters.table} onChange={(table) => setFilters({ ...filters, table })} />
        <Choice label="Channel" value={filters.channel} onChange={(channel) => setFilters({ ...filters, channel })} options={channels} />
        <DateField label="From" value={filters.from} onChange={(from) => setFilters({ ...filters, from })} />
        <DateField label="To" value={filters.to} onChange={(to) => setFilters({ ...filters, to })} />
      </div>
      <Button onPress={() => read.mutate()} isDisabled={read.isPending}>
        Read audit
      </Button>
      <ErrorText error={read.error ?? audit.error} />
      {audit.data && audit.data.records.length === 0 && audit.data.accessEvents.length === 0 ? (
        <p className="text-sm">No audit row.</p>
      ) : null}
      <Table aria-label="Audit records" className="w-full text-sm">
        <TableHeader className="text-left text-xs text-neutral-500">
          <Column isRowHeader className="py-1 font-medium">
            Time
          </Column>
          <Column className="py-1 font-medium">Table</Column>
          <Column className="py-1 font-medium">Op</Column>
          <Column className="py-1 font-medium">Actor</Column>
          <Column className="py-1 font-medium">Channel</Column>
        </TableHeader>
        <TableBody items={audit.data?.records ?? []} renderEmptyState={() => <span />}>
          {(record) => (
            <Row id={String(record.id)} className="border-t border-neutral-200 align-top">
              <Cell className="py-1 pr-2">{record.ts}</Cell>
              <Cell className="py-1 pr-2">
                {record.tableSchema}.{record.tableName}
              </Cell>
              <Cell className="py-1 pr-2">{record.op}</Cell>
              <Cell className="py-1 pr-2">{record.actorId ?? '—'}</Cell>
              <Cell className="py-1">{record.channel}</Cell>
            </Row>
          )}
        </TableBody>
      </Table>
      <h3 className="text-sm font-semibold">Access</h3>
      {audit.data?.accessEvents.map((event) => (
        <p key={event.id} className="text-sm">
          {event.at} · {event.action} · {statusLabel(event.outcome)} · {event.channel}
        </p>
      ))}
    </section>
  )
}

function FormShell({
  form,
  children,
  label = 'Save',
}: {
  form: { handleSubmit: () => Promise<unknown> }
  children: ReactNode
  label?: string
}) {
  const view = form as unknown as {
    Subscribe: (props: {
      selector: (state: { errorMap: { onSubmit?: unknown }; isSubmitting: boolean }) => unknown
      children: (value: unknown) => ReactNode
    }) => ReactNode
  }

  return (
    <form
      className="flex max-w-xl flex-col gap-2"
      onSubmit={(event) => {
        event.preventDefault()
        void form.handleSubmit()
      }}
    >
      {children}
      <view.Subscribe selector={(state) => state.errorMap.onSubmit}>
        {(error) => <ErrorText error={error} />}
      </view.Subscribe>
      <view.Subscribe selector={(state) => state.isSubmitting}>
        {(isSubmitting) => (
          <Button type="submit" isDisabled={Boolean(isSubmitting)}>
            {label}
          </Button>
        )}
      </view.Subscribe>
    </form>
  )
}
