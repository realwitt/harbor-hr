import { useForm, useSelector } from '@tanstack/react-form'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { api, idempotencyHeaders } from '../api'
import { hoursText, leaveTypeLabel, parseHours } from '../format'
import type { LeavePreview, LeaveType } from '../types'
import { useDebounced } from '../use-debounced'
import { Button, Choice, DateField, ErrorText, Page, TextField } from '../ui'
import { stepUp } from '../webauthn'

type LeaveDraft = {
  leaveTypeId: string
  start: string
  end: string
  hoursPerDay: number
  adminOverride: false
}

const hoursError = 'Hours per day must be greater than 0 and at most 24.'

function draftFrom(leaveTypeId: string, start: string, end: string, hoursPerDay: string): LeaveDraft | null {
  const hours = parseHours(hoursPerDay, false)
  if (!leaveTypeId || !start || !end || hours === null || hours <= 0 || hours > 24 || end < start) {
    return null
  }

  return { leaveTypeId, start, end, hoursPerDay: hours, adminOverride: false }
}

function inclusiveDays(start: string, end: string): number {
  const startMs = Date.parse(`${start}T00:00:00Z`)
  const endMs = Date.parse(`${end}T00:00:00Z`)
  return Math.round((endMs - startMs) / 86_400_000) + 1
}

export function LeaveNewPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const types = useQuery({
    queryKey: ['leave-types'],
    queryFn: () => api<LeaveType[]>('/api/leave/types'),
  })
  const form = useForm({
    defaultValues: {
      leaveTypeId: '',
      start: '',
      end: '',
      hoursPerDay: '8',
    },
  })
  const values = useSelector(form.store, (state) => state.values)
  const draftKey = `${values.leaveTypeId}|${values.start}|${values.end}|${values.hoursPerDay}`
  const debouncedKey = useDebounced(draftKey, 300)
  const settled = draftKey === debouncedKey
  const [debouncedType = '', debouncedStart = '', debouncedEnd = '', debouncedHours = ''] = debouncedKey.split('|')
  const draft = draftFrom(debouncedType, debouncedStart, debouncedEnd, debouncedHours)
  const live = draftFrom(values.leaveTypeId, values.start, values.end, values.hoursPerDay)
  const preview = useQuery({
    queryKey: ['leave-preview', debouncedType, debouncedStart, debouncedEnd, debouncedHours],
    enabled: draft !== null,
    staleTime: 60_000,
    refetchOnWindowFocus: false,
    queryFn: () =>
      api<LeavePreview>('/api/leave/preview', {
        method: 'POST',
        body: JSON.stringify(draft),
      }),
  })
  const quote = settled && draft ? preview.data : undefined
  const canRequest = Boolean(quote && quote.warnings.length === 0 && !preview.isFetching)
  const submit = useMutation({
    mutationFn: async () => {
      if (!quote || !draft || quote.warnings.length > 0) {
        throw new Error('The request is not valid.')
      }

      await stepUp('confirm_quote', quote.quoteId)
      await api('/api/leave/submit', {
        method: 'POST',
        headers: idempotencyHeaders(),
        body: JSON.stringify({
          quoteId: quote.quoteId,
          leaveTypeId: draft.leaveTypeId,
          start: draft.start,
          end: draft.end,
          hoursPerDay: draft.hoursPerDay,
          adminOverride: false,
        }),
      })
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['leave-requests'] })
      await queryClient.invalidateQueries({ queryKey: ['leave-balances'] })
      await navigate({ to: '/leave' })
    },
  })
  const parsedHours = parseHours(values.hoursPerDay, false)
  const hoursFieldError =
    values.hoursPerDay.trim() !== '' && (parsedHours === null || parsedHours <= 0 || parsedHours > 24) ? hoursError : undefined
  const endFieldError = values.start && values.end && values.end < values.start ? 'The end date is before the start date.' : undefined

  return (
    <Page title="Request leave">
      <ErrorText error={types.error} />
      <form
        className="flex max-w-md flex-col gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          if (canRequest) {
            submit.mutate()
          }
        }}
      >
        <form.Field name="leaveTypeId">
          {(field) => (
            <Choice
              label="Type"
              value={field.state.value}
              onChange={field.handleChange}
              options={(types.data ?? []).map((type) => ({ id: type.id, label: leaveTypeLabel(type.code) }))}
            />
          )}
        </form.Field>
        <form.Field name="start">
          {(field) => <DateField label="Start" value={field.state.value} onChange={field.handleChange} />}
        </form.Field>
        <form.Field name="end">
          {(field) => (
            <DateField label="End" value={field.state.value} onChange={field.handleChange} error={endFieldError} />
          )}
        </form.Field>
        <form.Field name="hoursPerDay">
          {(field) => (
            <TextField
              label="Hours per day"
              value={field.state.value}
              onChange={field.handleChange}
              onBlur={field.handleBlur}
              error={hoursFieldError}
            />
          )}
        </form.Field>
        <RequestSummary live={live} quote={quote} checking={draft !== null && (preview.isFetching || !settled)} />
        <Button type="submit" isDisabled={!canRequest || submit.isPending}>
          Request
        </Button>
        <ErrorText error={preview.error ?? submit.error} />
      </form>
    </Page>
  )
}

function RequestSummary({
  live,
  quote,
  checking,
}: {
  live: LeaveDraft | null
  quote: LeavePreview | undefined
  checking: boolean
}) {
  if (!live) {
    return <p className="text-sm">Choose a type, dates, and hours.</p>
  }

  const days = inclusiveDays(live.start, live.end)
  const projection = quote?.projection
  return (
    <section className="panel">
      <h2 className="text-sm font-semibold">This request</h2>
      <p className="text-sm">
        {days} {days === 1 ? 'day' : 'days'} · {hoursText(live.hoursPerDay * days)}
      </p>
      {checking ? <p className="text-sm">Checking the request.</p> : null}
      {projection?.hasBalance ? <p className="text-sm">Available {hoursText(projection.availableHours)}.</p> : null}
      {projection && !projection.hasBalance ? <p className="text-sm">This leave type has no balance.</p> : null}
      {quote && quote.warnings.length === 0 && !checking ? <p className="text-sm">You can request this leave.</p> : null}
      {quote?.warnings.map((warning, index) => (
        <p key={`${warning.code}-${index}`} className="invalid text-sm">
          {warning.message}
        </p>
      ))}
    </section>
  )
}
