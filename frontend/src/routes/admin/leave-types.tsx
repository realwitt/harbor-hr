import { useForm } from '@tanstack/react-form'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../../api'
import { hoursText, parseHours } from '../../format'
import type { LeaveType } from '../../types'
import { Button, Choice, ErrorText, Page, Prompt, TextField, fieldErrors } from '../../ui'
import { FormShell } from './shared'

const models = [
  { id: 'accrued', label: 'Accrued' },
  { id: 'instant', label: 'Instant' },
  { id: 'unpaid', label: 'Unpaid' },
]
const boundaries = [
  { id: 'calendar', label: 'Calendar' },
  { id: 'anniversary', label: 'Anniversary' },
]

function optionalNumber(text: string): number | null {
  const trimmed = text.trim()
  if (!trimmed) {
    return null
  }

  return parseHours(trimmed, false)
}

function modelLabel(model: string): string {
  return models.find((item) => item.id === model)?.label ?? model
}

export function LeaveTypesPage() {
  const [open, setOpen] = useState(false)
  const types = useQuery({
    queryKey: ['admin-leave-types'],
    queryFn: () => api<LeaveType[]>('/api/admin/leave-types'),
  })

  return (
    <Page
      title="Leave types"
      action={
        <Button onPress={() => setOpen(true)}>Add leave type</Button>
      }
    >
      <p className="subtle">Grant rules for each leave type.</p>
      <ErrorText error={types.error} />
      <div className="table-wrap">
        <Table aria-label="Leave types">
          <TableHeader>
            <Column isRowHeader>Code</Column>
            <Column>Model</Column>
            <Column>Grant</Column>
            <Column>Year</Column>
            <Column>Carry cap</Column>
            <Column>Max balance</Column>
            <Column>Wait</Column>
          </TableHeader>
          <TableBody items={types.data ?? []} renderEmptyState={() => <p className="empty-row">No leave type.</p>}>
            {(type) => (
              <Row id={type.id}>
                <Cell>{type.code}</Cell>
                <Cell>{modelLabel(type.model)}</Cell>
                <Cell>{type.model === 'unpaid' ? 'No grant' : hoursText(type.hoursPerGrant)}</Cell>
                <Cell>{type.yearBoundary === 'anniversary' ? 'Anniversary' : 'Calendar'}</Cell>
                <Cell>{hoursText(type.carryCapHours)}</Cell>
                <Cell>{hoursText(type.maxBalanceHours)}</Cell>
                <Cell>{type.waitingDays === 1 ? '1 day' : `${type.waitingDays} days`}</Cell>
              </Row>
            )}
          </TableBody>
        </Table>
      </div>
      <LeaveTypeDialog open={open} onOpenChange={setOpen} />
    </Page>
  )
}

function LeaveTypeDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const queryClient = useQueryClient()
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
      const hours = value.model === 'unpaid' ? null : optionalNumber(value.hoursPerGrant)
      if (value.model !== 'unpaid' && (hours === null || hours <= 0)) {
        throw new Error('Hours per grant must be greater than 0.')
      }

      const carry = optionalNumber(value.carryCapHours)
      const max = optionalNumber(value.maxBalanceHours)
      if (value.carryCapHours.trim() && (carry === null || carry < 0)) {
        throw new Error('The carry cap is not valid.')
      }

      if (value.maxBalanceHours.trim() && (max === null || max < 0)) {
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
      form.reset()
      onOpenChange(false)
    },
  })

  return (
    <Prompt open={open} onOpenChange={onOpenChange} title="Add leave type">
      <FormShell form={form} label="Save leave type" onCancel={() => onOpenChange(false)}>
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
    </Prompt>
  )
}
