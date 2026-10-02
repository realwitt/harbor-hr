import { useForm } from '@tanstack/react-form'
import { useState } from 'react'
import { api } from '../../api'
import { Button, Choice, DateField, ErrorText, Page, TextField, fieldErrors } from '../../ui'
import { FormShell, useEmployees } from './shared'

const roles = [
  { id: 'employee', label: 'Employee' },
  { id: 'hr_admin', label: 'HR admin' },
]

export function InvitesPage() {
  const people = useEmployees()
  const [path, setPath] = useState('')
  const [copied, setCopied] = useState(false)
  const [copyError, setCopyError] = useState('')
  const managers = [
    { id: 'none', label: 'No manager' },
    ...(people.data ?? []).map((person) => ({ id: person.id, label: person.name })),
  ]
  const form = useForm({
    defaultValues: {
      email: '',
      name: '',
      role: 'employee',
      managerId: 'none',
      hiredOn: '',
      jurisdiction: '',
      timezone: 'America/New_York',
    },
    onSubmit: async ({ value }) => {
      const result = await api<{ path: string }>('/api/auth/invites', {
        method: 'POST',
        body: JSON.stringify({
          email: value.email.trim(),
          name: value.name.trim(),
          role: value.role,
          managerId: value.managerId === 'none' ? null : value.managerId,
          hiredOn: value.hiredOn,
          jurisdiction: value.jurisdiction.trim(),
          timezone: value.timezone.trim() || 'America/New_York',
        }),
      })
      setPath(result.path)
      setCopied(false)
      setCopyError('')
    },
  })

  return (
    <Page title="Invites">
      <p className="subtle">Create an account invite. The person opens the path to set a passkey.</p>
      <ErrorText error={people.error} />
      <FormShell form={form} label="Create invite">
        <div className="form-grid">
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
            {(field) => <Choice label="Manager" value={field.state.value} onChange={field.handleChange} options={managers} />}
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
        </div>
      </FormShell>
      {path ? (
        <div className="panel invite-result">
          <p className="section-title">Invite path</p>
          <p className="invite-path">{path}</p>
          <div className="row-actions">
            <Button
              quiet
              onPress={() => {
                void navigator.clipboard.writeText(path).then(
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
