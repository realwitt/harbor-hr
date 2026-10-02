import { useForm } from '@tanstack/react-form'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../../api'
import { dateRange, localIsoDate, mediumDate } from '../../format'
import type { PayPeriod } from '../../types'
import { Button, DateField, ErrorText, Page, Prompt, fieldErrors } from '../../ui'
import { FormShell, YearSwitch } from './shared'

const quarters = [
  { id: 'q1', label: 'January – March', start: 1, end: 3 },
  { id: 'q2', label: 'April – June', start: 4, end: 6 },
  { id: 'q3', label: 'July – September', start: 7, end: 9 },
  { id: 'q4', label: 'October – December', start: 10, end: 12 },
]

function overlapsYear(period: PayPeriod, year: number): boolean {
  return period.startsOn <= `${year}-12-31` && period.endsOn >= `${year}-01-01`
}

function anchorMonth(period: PayPeriod, year: number): number {
  if (period.startsOn.slice(0, 4) === String(year)) {
    return Number(period.startsOn.slice(5, 7))
  }

  return Number(period.endsOn.slice(5, 7))
}

function periodState(period: PayPeriod, today: string): 'Current' | 'Past' | 'Upcoming' {
  if (period.startsOn <= today && today <= period.endsOn) {
    return 'Current'
  }

  return period.endsOn < today ? 'Past' : 'Upcoming'
}

export function PayPeriodsPage() {
  const today = localIsoDate()
  const [year, setYear] = useState(() => Number(today.slice(0, 4)))
  const [open, setOpen] = useState(false)
  const periods = useQuery({
    queryKey: ['pay-periods'],
    queryFn: () => api<PayPeriod[]>('/api/admin/pay-periods'),
  })
  const visible = (periods.data ?? []).filter((period) => overlapsYear(period, year))

  return (
    <Page
      title="Pay periods"
      action={
        <div className="toolbar">
          <YearSwitch year={year} onChange={setYear} />
          <Button onPress={() => setOpen(true)}>Add pay period</Button>
        </div>
      }
    >
      <p className="subtle">Periods that overlap {year}. The current period is marked.</p>
      <ErrorText error={periods.error} />
      <div className="quarter-grid">
        {quarters.map((quarter) => {
          const rows = visible.filter((period) => {
            const month = anchorMonth(period, year)
            return month >= quarter.start && month <= quarter.end
          })
          return (
            <section key={quarter.id} className="panel">
              <h2 className="section-title">{quarter.label}</h2>
              <div className="table-wrap">
                <Table aria-label={quarter.label}>
                  <TableHeader>
                    <Column isRowHeader>Period</Column>
                    <Column>Pay date</Column>
                    <Column>Status</Column>
                  </TableHeader>
                  <TableBody items={rows} renderEmptyState={() => <p className="empty-row">No period.</p>}>
                    {(period) => {
                      const state = periodState(period, today)
                      return (
                        <Row id={period.id} className={state === 'Current' ? 'row-current' : undefined}>
                          <Cell>{dateRange(period.startsOn, period.endsOn)}</Cell>
                          <Cell>{mediumDate(period.payDate)}</Cell>
                          <Cell>{state}</Cell>
                        </Row>
                      )
                    }}
                  </TableBody>
                </Table>
              </div>
            </section>
          )
        })}
      </div>
      <PayPeriodDialog open={open} onOpenChange={setOpen} />
    </Page>
  )
}

function PayPeriodDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const queryClient = useQueryClient()
  const form = useForm({
    defaultValues: { startsOn: '', endsOn: '', payDate: '' },
    onSubmit: async ({ value }) => {
      if (value.endsOn < value.startsOn) {
        throw new Error('The end date is before the start date.')
      }

      await api('/api/admin/pay-periods', {
        method: 'POST',
        body: JSON.stringify(value),
      })
      await queryClient.invalidateQueries({ queryKey: ['pay-periods'] })
      form.reset()
      onOpenChange(false)
    },
  })

  return (
    <Prompt open={open} onOpenChange={onOpenChange} title="Add pay period">
      <FormShell form={form} label="Save pay period" onCancel={() => onOpenChange(false)}>
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
    </Prompt>
  )
}
