import { useForm } from '@tanstack/react-form'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from '@tanstack/react-router'
import { useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../../api'
import { Button, Choice, DateField, ErrorText, Page, TextField, fieldErrors } from '../../ui'
import { FormShell, useEmployees } from './shared'

type JoinRequest = {
  id: string
  email: string
  name: string
  note: string | null
  status: string
  createdAt: string
}

const roles = [
  { id: 'employee', label: 'Employee' },
  { id: 'hr_admin', label: 'HR admin' },
]

function statusLabel(status: string): string {
  if (status === 'pending') {
    return 'Pending'
  }
  if (status === 'approved') {
    return 'Approved'
  }
  if (status === 'dismissed') {
    return 'Dismissed'
  }
  return status
}

function todayInput(): string {
  const now = new Date()
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')
  return `${now.getFullYear()}-${month}-${day}`
}

export function JoinRequestsPage() {
  const list = useQuery({
    queryKey: ['join-requests'],
    queryFn: () => api<JoinRequest[]>('/api/auth/join-requests'),
  })

  return (
    <Page title="Join requests">
      <ErrorText error={list.error} />
      {list.isPending ? <p className="subtle">Loading join requests.</p> : null}
      {list.data ? (
        <div className="table-wrap">
          <Table aria-label="Join requests">
            <TableHeader>
              <Column isRowHeader>Name</Column>
              <Column>Email</Column>
              <Column>Status</Column>
            </TableHeader>
            <TableBody items={list.data} renderEmptyState={() => <p className="empty-row">No join requests.</p>}>
              {(row) => (
                <Row id={row.id}>
                  <Cell>
                    <Link to="/admin/join-requests/$id" params={{ id: row.id }}>
                      {row.name}
                    </Link>
                  </Cell>
                  <Cell>{row.email}</Cell>
                  <Cell>{statusLabel(row.status)}</Cell>
                </Row>
              )}
            </TableBody>
          </Table>
        </div>
      ) : null}
    </Page>
  )
}

export function JoinRequestPage() {
  const { id } = useParams({ from: '/auth/ready/admin/join-requests/$id' })
  const queryClient = useQueryClient()
  const people = useEmployees()
  const request = useQuery({
    queryKey: ['join-request', id],
    queryFn: () => api<JoinRequest>(`/api/auth/join-requests/${id}`),
  })
  const [setup, setSetup] = useState<{ path: string; mailSent: boolean } | null>(null)
  const [copied, setCopied] = useState(false)
  const [copyError, setCopyError] = useState('')
  const dismiss = useMutation({
    mutationFn: () => api(`/api/auth/join-requests/${id}/dismiss`, { method: 'POST' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['join-request', id] })
      await queryClient.invalidateQueries({ queryKey: ['join-requests'] })
    },
  })
  const managers = [
    { id: 'none', label: 'No manager' },
    ...(people.data ?? []).map((person) => ({ id: person.id, label: person.name })),
  ]
  const form = useForm({
    defaultValues: {
      role: 'employee',
      managerId: 'none',
      hiredOn: todayInput(),
      jurisdiction: 'US-NC',
      timezone: 'America/New_York',
    },
    onSubmit: async ({ value }) => {
      const result = await api<{ path: string; mailSent: boolean }>(`/api/auth/join-requests/${id}/approve`, {
        method: 'POST',
        body: JSON.stringify({
          role: value.role,
          managerId: value.managerId === 'none' ? null : value.managerId,
          hiredOn: value.hiredOn,
          jurisdiction: value.jurisdiction.trim(),
          timezone: value.timezone.trim() || 'America/New_York',
        }),
      })
      setSetup(result)
      setCopied(false)
      setCopyError('')
      await queryClient.invalidateQueries({ queryKey: ['join-request', id] })
      await queryClient.invalidateQueries({ queryKey: ['join-requests'] })
    },
  })
  const row = request.data
  const closed = row && row.status !== 'pending'

  return (
    <Page title="Join request">
      <ErrorText error={request.error} />
      <ErrorText error={people.error} />
      {request.isPending ? <p className="subtle">Loading the request.</p> : null}
      {row ? (
        <>
          <p>
            {row.name} ({row.email})
          </p>
          <p className="subtle">{row.note ?? 'No note.'}</p>
          <p className="subtle">{statusLabel(row.status)}</p>
        </>
      ) : null}
      {row?.status === 'pending' && !setup ? (
        <>
          <FormShell form={form} label="Set up account">
            <div className="form-grid">
              <form.Field name="role">
                {(field) => <Choice label="Role" value={field.state.value} onChange={field.handleChange} options={roles} />}
              </form.Field>
              <form.Field name="managerId">
                {(field) => (
                  <Choice label="Manager" value={field.state.value} onChange={field.handleChange} options={managers} />
                )}
              </form.Field>
              <form.Field name="hiredOn" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
                {(field) => (
                  <DateField
                    label="Hired on"
                    value={field.state.value}
                    onChange={field.handleChange}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              <form.Field
                name="jurisdiction"
                validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The jurisdiction is required.') }}
              >
                {(field) => (
                  <TextField
                    label="Jurisdiction"
                    value={field.state.value}
                    onChange={field.handleChange}
                    onBlur={field.handleBlur}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              <form.Field name="timezone">
                {(field) => (
                  <TextField label="Timezone" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />
                )}
              </form.Field>
            </div>
          </FormShell>
          <div className="row-actions">
            <Button quiet onPress={() => dismiss.mutate()} isDisabled={dismiss.isPending}>
              Dismiss
            </Button>
          </div>
          <ErrorText error={dismiss.error} />
        </>
      ) : null}
      {closed && !setup ? (
        <p className="subtle">{row.status === 'approved' ? 'This request is approved.' : 'This request is dismissed.'}</p>
      ) : null}
      {setup ? (
        <div className="panel invite-result">
          <p className="subtle">
            {setup.mailSent ? 'The invite email is sent.' : 'Email is not configured on this server.'}
          </p>
          <p className="section-title">Invite path</p>
          <p className="invite-path">{setup.path}</p>
          <div className="row-actions">
            <Button
              quiet
              onPress={() => {
                void navigator.clipboard.writeText(setup.path).then(
                  () => {
                    setCopied(true)
                    setCopyError('')
                  },
                  () => setCopyError('The path was not copied.'),
                )
              }}
            >
              Copy path
            </Button>
          </div>
          {copied ? <p className="subtle">Copied.</p> : null}
          {copyError ? <p className="field-error">{copyError}</p> : null}
        </div>
      ) : null}
    </Page>
  )
}
