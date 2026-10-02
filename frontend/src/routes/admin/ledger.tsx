import { useForm } from '@tanstack/react-form'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../api'
import { parseHours } from '../../format'
import type { LeaveType } from '../../types'
import { Choice, DateField, ErrorText, Page, TextField, fieldErrors } from '../../ui'
import { FormShell, useEmployees } from './shared'

export function LedgerPage() {
  const people = useEmployees()
  const types = useQuery({
    queryKey: ['admin-leave-types'],
    queryFn: () => api<LeaveType[]>('/api/admin/leave-types'),
  })
  const [saved, setSaved] = useState('')
  const form = useForm({
    defaultValues: { employeeId: '', leaveTypeId: '', hours: '', effectiveOn: '', note: '' },
    onSubmit: async ({ value }) => {
      const hours = parseHours(value.hours, true)
      if (hours === null) {
        throw new Error('The hours are not valid.')
      }

      await api('/api/admin/ledger', {
        method: 'POST',
        body: JSON.stringify({
          employeeId: value.employeeId,
          leaveTypeId: value.leaveTypeId,
          hours,
          effectiveOn: value.effectiveOn,
          note: value.note.trim(),
        }),
      })
      const person = people.data?.find((row) => row.id === value.employeeId)
      setSaved(`Saved an adjustment for ${person?.name ?? 'the person'}.`)
      form.reset()
    },
  })
  const personOptions = (people.data ?? []).map((person) => ({ id: person.id, label: person.name }))
  const typeOptions = (types.data ?? []).map((type) => ({ id: type.id, label: type.code }))

  return (
    <Page title="Ledger">
      <p className="subtle">Add an hour adjustment for one person and one leave type.</p>
      <ErrorText error={people.error ?? types.error} />
      <FormShell form={form} label="Add adjustment">
        <div className="form-grid">
          <form.Field name="employeeId" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a person.') }}>
            {(field) => (
              <Choice label="Person" value={field.state.value} onChange={field.handleChange} options={personOptions} error={fieldErrors(field.state.meta.errors)} />
            )}
          </form.Field>
          <form.Field name="leaveTypeId" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a leave type.') }}>
            {(field) => (
              <Choice label="Leave type" value={field.state.value} onChange={field.handleChange} options={typeOptions} error={fieldErrors(field.state.meta.errors)} />
            )}
          </form.Field>
          <form.Field name="hours">
            {(field) => <TextField label="Hours" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
          </form.Field>
          <form.Field name="effectiveOn" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a date.') }}>
            {(field) => (
              <DateField label="Effective on" value={field.state.value} onChange={field.handleChange} error={fieldErrors(field.state.meta.errors)} />
            )}
          </form.Field>
          <form.Field name="note" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The note is required.') }}>
            {(field) => (
              <TextField label="Note" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
            )}
          </form.Field>
        </div>
      </FormShell>
      {saved ? <p>{saved}</p> : null}
    </Page>
  )
}
