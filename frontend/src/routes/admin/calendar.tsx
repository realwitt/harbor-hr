import { CalendarDate } from '@internationalized/date'
import { useForm } from '@tanstack/react-form'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../api'
import { localIsoDate, mediumDate } from '../../format'
import type { Blackout, Holiday } from '../../types'
import { Button, ErrorText, Page, TextField, fieldErrors } from '../../ui'
import { FormShell, YearSwitch } from './shared'

const weekdays = ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa']

function monthWeeks(year: number, month: number): CalendarDate[][] {
  const first = new CalendarDate(year, month, 1)
  let cursor = first.subtract({ days: first.toDate('UTC').getUTCDay() })
  const weeks: CalendarDate[][] = []
  do {
    const week: CalendarDate[] = []
    for (let index = 0; index < 7; index += 1) {
      week.push(cursor)
      cursor = cursor.add({ days: 1 })
    }
    weeks.push(week)
  } while (cursor.year === year && cursor.month === month)

  return weeks
}

function monthName(year: number, month: number): string {
  return new Intl.DateTimeFormat('en-US', { month: 'long', timeZone: 'UTC' }).format(
    new Date(Date.UTC(year, month - 1, 1)),
  )
}

export function CalendarPage() {
  const today = localIsoDate()
  const [year, setYear] = useState(() => Number(today.slice(0, 4)))
  const [selected, setSelected] = useState('')
  const queryClient = useQueryClient()
  const holidays = useQuery({
    queryKey: ['holidays'],
    queryFn: () => api<Holiday[]>('/api/admin/holidays'),
  })
  const blackouts = useQuery({
    queryKey: ['blackouts'],
    queryFn: () => api<Blackout[]>('/api/admin/blackouts'),
  })
  const holidayOn = new Map((holidays.data ?? []).map((row) => [row.on, row.name]))
  const blackoutOn = new Map((blackouts.data ?? []).map((row) => [row.on, row.reason]))
  const yearHolidays = (holidays.data ?? []).filter((row) => row.on.startsWith(`${year}-`))
  const yearBlackouts = (blackouts.data ?? []).filter((row) => row.on.startsWith(`${year}-`))

  function changeYear(next: number) {
    setYear(next)
    setSelected((current) => (current.startsWith(`${next}-`) ? current : ''))
  }

  return (
    <Page title="Calendar" action={<YearSwitch year={year} onChange={changeYear} />}>
      <p className="subtle">Company holidays and blackout dates. Select a day to change it.</p>
      <div className="legend">
        <span className="legend-item">
          <span className="swatch" data-holiday="" /> Holiday
        </span>
        <span className="legend-item">
          <span className="swatch" data-blackout="" /> Blackout
        </span>
      </div>
      <ErrorText error={holidays.error ?? blackouts.error} />
      <DayPanel
        selected={selected}
        holiday={selected ? holidayOn.get(selected) : undefined}
        blackout={selected ? blackoutOn.get(selected) : undefined}
        onSaved={async () => {
          await queryClient.invalidateQueries({ queryKey: ['holidays'] })
          await queryClient.invalidateQueries({ queryKey: ['blackouts'] })
        }}
      />
      <div className="year-board">
        {Array.from({ length: 12 }, (_, index) => {
          const month = index + 1
          return (
            <MonthCard
              key={month}
              year={year}
              month={month}
              today={today}
              selected={selected}
              holidayOn={holidayOn}
              blackoutOn={blackoutOn}
              onSelect={setSelected}
            />
          )
        })}
      </div>
      <EventList holidays={yearHolidays} blackouts={yearBlackouts} onSelect={setSelected} />
    </Page>
  )
}

function MonthCard({
  year,
  month,
  today,
  selected,
  holidayOn,
  blackoutOn,
  onSelect,
}: {
  year: number
  month: number
  today: string
  selected: string
  holidayOn: Map<string, string>
  blackoutOn: Map<string, string>
  onSelect: (iso: string) => void
}) {
  return (
    <section className="month-card">
      <h2 className="section-title">{monthName(year, month)}</h2>
      <div className="month-grid" aria-hidden="true">
        {weekdays.map((day) => (
          <span key={day} className="weekday">
            {day}
          </span>
        ))}
      </div>
      <div className="month-grid">
        {monthWeeks(year, month).flat().map((date) => {
          const iso = date.toString()
          if (date.year !== year || date.month !== month) {
            return <span key={iso} className="day-pad" />
          }

          const holiday = holidayOn.get(iso)
          const blackout = blackoutOn.get(iso)
          const label = [mediumDate(iso), holiday, blackout].filter(Boolean).join(', ')
          return (
            <button
              key={iso}
              type="button"
              className="day-cell"
              data-holiday={holiday ? '' : undefined}
              data-blackout={blackout ? '' : undefined}
              data-selected={selected === iso ? '' : undefined}
              data-today={today === iso ? '' : undefined}
              aria-pressed={selected === iso}
              aria-label={label}
              onClick={() => onSelect(iso)}
            >
              {date.day}
            </button>
          )
        })}
      </div>
    </section>
  )
}

function DayPanel({
  selected,
  holiday,
  blackout,
  onSaved,
}: {
  selected: string
  holiday?: string
  blackout?: string
  onSaved: () => Promise<void>
}) {
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: (on: string) => api(`/api/admin/blackouts/${encodeURIComponent(on)}`, { method: 'DELETE' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['blackouts'] })
    },
  })

  if (!selected) {
    return <p className="subtle">Select a day.</p>
  }

  return (
    <section className="panel day-panel">
      <h2 className="section-title">{mediumDate(selected)}</h2>
      <ErrorText error={remove.error} />
      <div className="form-grid">
        {holiday ? <p>Holiday: {holiday}</p> : <HolidayForm key={`holiday-${selected}`} on={selected} onSaved={onSaved} />}
        {blackout ? (
          <div className="row-line">
            <p>Blackout: {blackout}</p>
            <Button quiet onPress={() => remove.mutate(selected)} isDisabled={remove.isPending}>
              Remove blackout
            </Button>
          </div>
        ) : (
          <BlackoutForm key={`blackout-${selected}`} on={selected} onSaved={onSaved} />
        )}
      </div>
    </section>
  )
}

function HolidayForm({ on, onSaved }: { on: string; onSaved: () => Promise<void> }) {
  const form = useForm({
    defaultValues: { name: '' },
    onSubmit: async ({ value }) => {
      await api('/api/admin/holidays', {
        method: 'POST',
        body: JSON.stringify({ on, name: value.name.trim() }),
      })
      form.reset()
      await onSaved()
    },
  })

  return (
    <FormShell form={form} label="Add holiday">
      <form.Field name="name" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The name is required.') }}>
        {(field) => (
          <TextField label="Holiday name" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
        )}
      </form.Field>
    </FormShell>
  )
}

function BlackoutForm({ on, onSaved }: { on: string; onSaved: () => Promise<void> }) {
  const form = useForm({
    defaultValues: { reason: '' },
    onSubmit: async ({ value }) => {
      await api('/api/admin/blackouts', {
        method: 'POST',
        body: JSON.stringify({ on, reason: value.reason.trim() }),
      })
      form.reset()
      await onSaved()
    },
  })

  return (
    <FormShell form={form} label="Add blackout">
      <form.Field name="reason" validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'The reason is required.') }}>
        {(field) => (
          <TextField label="Blackout reason" value={field.state.value} onChange={field.handleChange} onBlur={field.handleBlur} error={fieldErrors(field.state.meta.errors)} />
        )}
      </form.Field>
    </FormShell>
  )
}

function EventList({
  holidays,
  blackouts,
  onSelect,
}: {
  holidays: Holiday[]
  blackouts: Blackout[]
  onSelect: (iso: string) => void
}) {
  const events = [
    ...holidays.map((row) => ({ on: row.on, kind: 'Holiday', text: row.name })),
    ...blackouts.map((row) => ({ on: row.on, kind: 'Blackout', text: row.reason })),
  ].sort((a, b) => a.on.localeCompare(b.on))

  if (events.length === 0) {
    return <p className="subtle">No holiday or blackout this year.</p>
  }

  return (
    <ul className="event-list">
      {events.map((event) => (
        <li key={`${event.kind}-${event.on}`}>
          <button type="button" className="event-link" onClick={() => onSelect(event.on)}>
            <span>{mediumDate(event.on)}</span>
            <span>{event.kind}</span>
            <span>{event.text}</span>
          </button>
        </li>
      ))}
    </ul>
  )
}
