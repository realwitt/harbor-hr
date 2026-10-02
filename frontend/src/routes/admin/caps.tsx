import { useForm } from '@tanstack/react-form'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../../api'
import { coverageLabel, kindLabel, money, parseCents } from '../../format'
import type { DeductionCap } from '../../types'
import { Button, Choice, ErrorText, Page, Prompt, TextField, fieldErrors } from '../../ui'
import { FormShell } from './shared'

const kinds = [
  { id: 'hsa', label: 'HSA' },
  { id: 'health_fsa', label: 'Health FSA' },
  { id: 'dependent_care_fsa', label: 'Dependent care FSA' },
]
const coverages = [
  { id: 'self', label: 'Self' },
  { id: 'family', label: 'Family' },
  { id: 'catch_up', label: 'Catch-up' },
  { id: 'employee', label: 'Employee' },
]

export function CapsPage() {
  const [open, setOpen] = useState(false)
  const caps = useQuery({
    queryKey: ['deduction-caps'],
    queryFn: () => api<DeductionCap[]>('/api/admin/deduction-caps'),
  })
  const rows = [...(caps.data ?? [])]
    .sort((a, b) => b.taxYear - a.taxYear || a.kind.localeCompare(b.kind) || a.coverage.localeCompare(b.coverage))
    .map((cap) => ({ ...cap, id: `${cap.taxYear}-${cap.kind}-${cap.coverage}` }))

  return (
    <Page title="Deduction caps" action={<Button onPress={() => setOpen(true)}>Add cap</Button>}>
      <p className="subtle">Yearly limits for HSA and FSA.</p>
      <ErrorText error={caps.error} />
      <div className="table-wrap">
        <Table aria-label="Deduction caps">
          <TableHeader>
            <Column isRowHeader>Year</Column>
            <Column>Type</Column>
            <Column>Coverage</Column>
            <Column>Limit</Column>
          </TableHeader>
          <TableBody items={rows} renderEmptyState={() => <p className="empty-row">No deduction cap.</p>}>
            {(cap) => (
              <Row id={cap.id}>
                <Cell>{cap.taxYear}</Cell>
                <Cell>{kindLabel(cap.kind)}</Cell>
                <Cell>{coverageLabel(cap.coverage)}</Cell>
                <Cell>{money(cap.limitCents)}</Cell>
              </Row>
            )}
          </TableBody>
        </Table>
      </div>
      <CapDialog open={open} onOpenChange={setOpen} />
    </Page>
  )
}

function CapDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const queryClient = useQueryClient()
  const form = useForm({
    defaultValues: { taxYear: String(new Date().getFullYear()), kind: 'hsa', coverage: 'self', limit: '' },
    onSubmit: async ({ value }) => {
      const year = Number(value.taxYear)
      const cents = parseCents(value.limit)
      if (!Number.isInteger(year) || year < 2000) {
        throw new Error('The tax year is not valid.')
      }

      if (cents === null || cents <= 0) {
        throw new Error('Enter a limit greater than 0.')
      }

      await api('/api/admin/deduction-caps', {
        method: 'POST',
        body: JSON.stringify({
          taxYear: year,
          kind: value.kind,
          coverage: value.coverage,
          limitCents: cents,
        }),
      })
      await queryClient.invalidateQueries({ queryKey: ['deduction-caps'] })
      form.reset()
      onOpenChange(false)
    },
  })

  return (
    <Prompt open={open} onOpenChange={onOpenChange} title="Add deduction cap">
      <FormShell form={form} label="Save cap" onCancel={() => onOpenChange(false)}>
        <form.Field name="taxYear">
          {(field) => <TextField label="Tax year" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
        <form.Field name="kind">
          {(field) => <Choice label="Type" value={field.state.value} onChange={field.handleChange} options={kinds} />}
        </form.Field>
        <form.Field name="coverage" validators={{ onSubmit: ({ value }) => (value ? undefined : 'Pick a coverage.') }}>
          {(field) => (
            <Choice label="Coverage" value={field.state.value} onChange={field.handleChange} options={coverages} error={fieldErrors(field.state.meta.errors)} />
          )}
        </form.Field>
        <form.Field name="limit">
          {(field) => <TextField label="Yearly limit in dollars" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} />}
        </form.Field>
      </FormShell>
    </Prompt>
  )
}
