import { useForm } from '@tanstack/react-form'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { api, idempotencyHeaders } from '../api'
import { hoursText, parseHours } from '../format'
import type { LeaveBalance, LeavePreview, LeaveType } from '../types'
import { useDebounced } from '../use-debounced'
import { Button, Choice, DateField, ErrorText, Page, TextField, fieldErrors } from '../ui'
import { stepUp } from '../webauthn'

type LeaveDraft = {
  leaveTypeId: string
  start: string
  end: string
  hoursPerDay: number
  adminOverride: false
}

export function LeaveNewPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [step, setStep] = useState<'request' | 'confirm'>('request')
  const [focusDate, setFocusDate] = useState('')
  const projectionDate = useDebounced(focusDate, 200)
  const types = useQuery({
    queryKey: ['leave-types'],
    queryFn: () => api<LeaveType[]>('/api/leave/types'),
  })
  const projection = useQuery({
    queryKey: ['leave-balances', projectionDate],
    enabled: projectionDate.length > 0,
    queryFn: () => api<LeaveBalance[]>(`/api/leave/balances?on=${encodeURIComponent(projectionDate)}`),
  })
  const preview = useMutation({
    mutationFn: (body: LeaveDraft) =>
      api<LeavePreview>('/api/leave/preview', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
  })
  const submit = useMutation({
    mutationFn: async (quote: LeavePreview) => {
      const body = preview.variables
      if (!body) {
        throw new Error('Preview the request first.')
      }

      await stepUp('confirm_quote', quote.quoteId)
      await api('/api/leave/submit', {
        method: 'POST',
        headers: idempotencyHeaders(),
        body: JSON.stringify({
          quoteId: quote.quoteId,
          leaveTypeId: body.leaveTypeId,
          start: body.start,
          end: body.end,
          hoursPerDay: body.hoursPerDay,
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
  const form = useForm({
    defaultValues: {
      request: {
        leaveTypeId: '',
        start: '',
        end: '',
        hoursPerDay: '8',
      },
      confirm: {
        ready: true,
      },
    },
  })

  return (
    <Page title="Request leave">
      <ErrorText error={types.error} />
      {step === 'request' ? (
        <form.FormGroup
          name="request"
          validators={{
            onSubmit: ({ value }) => {
              if (value.start && value.end && value.end < value.start) {
                return { fields: { end: 'The end date is before the start date.' } }
              }

              return undefined
            },
          }}
          onGroupSubmit={async ({ value }) => {
            const hours = parseHours(value.hoursPerDay, false)
            if (hours === null) {
              return
            }

            await preview.mutateAsync({
              leaveTypeId: value.leaveTypeId,
              start: value.start,
              end: value.end,
              hoursPerDay: hours,
              adminOverride: false,
            })
            setStep('confirm')
          }}
        >
          {(group) => (
            <form
              className="flex max-w-md flex-col gap-2"
              onSubmit={(event) => {
                event.preventDefault()
                void group.handleSubmit()
              }}
            >
              <form.Field
                name="request.leaveTypeId"
                validators={{
                  onSubmit: ({ value }) => (value ? undefined : 'Pick a leave type.'),
                }}
              >
                {(field) => (
                  <Choice
                    label="Type"
                    value={field.state.value}
                    onChange={field.handleChange}
                    error={fieldErrors(field.state.meta.errors)}
                    options={(types.data ?? []).map((type) => ({ id: type.id, label: type.code }))}
                  />
                )}
              </form.Field>
              <form.Field
                name="request.start"
                validators={{
                  onSubmit: ({ value }) => (value ? undefined : 'Pick a date.'),
                }}
              >
                {(field) => (
                  <DateField
                    label="Start"
                    value={field.state.value}
                    onChange={field.handleChange}
                    onFocusDate={setFocusDate}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              <form.Field
                name="request.end"
                validators={{
                  onSubmit: ({ value }) => (value ? undefined : 'Pick a date.'),
                }}
              >
                {(field) => (
                  <DateField
                    label="End"
                    value={field.state.value}
                    onChange={field.handleChange}
                    onFocusDate={setFocusDate}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              <form.Field
                name="request.hoursPerDay"
                validators={{
                  onSubmit: ({ value }) => {
                    const hours = parseHours(value, false)
                    if (hours === null || hours <= 0 || hours > 24) {
                      return 'Hours per day must be greater than 0 and at most 24.'
                    }

                    return undefined
                  },
                }}
              >
                {(field) => (
                  <TextField
                    label="Hours per day"
                    value={field.state.value}
                    onChange={field.handleChange}
                    onBlur={field.handleBlur}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              <ErrorText error={group.state.meta.errors} />
              <Button type="submit" isDisabled={preview.isPending}>
                Preview
              </Button>
              <ErrorText error={preview.error} />
            </form>
          )}
        </form.FormGroup>
      ) : null}
      {step === 'confirm' && preview.data ? (
        <form.FormGroup
          name="confirm"
          onGroupSubmit={async () => {
            if (!preview.data) {
              return
            }

            await submit.mutateAsync(preview.data)
          }}
        >
          {(group) => (
            <form
              className="flex max-w-md flex-col gap-2"
              onSubmit={(event) => {
                event.preventDefault()
                void group.handleSubmit()
              }}
            >
              <QuoteView preview={preview.data!} />
              <div className="flex gap-2">
                <Button
                  quiet
                  onPress={() => {
                    preview.reset()
                    submit.reset()
                    setStep('request')
                  }}
                >
                  Back
                </Button>
                <Button type="submit" isDisabled={submit.isPending}>
                  Confirm
                </Button>
              </div>
              <ErrorText error={submit.error} />
            </form>
          )}
        </form.FormGroup>
      ) : null}
      <ProjectionPanel date={projectionDate} rows={projection.data} error={projection.error} pending={projection.isFetching} />
    </Page>
  )
}

function QuoteView({ preview }: { preview: LeavePreview }) {
  const projection = preview.projection
  return (
    <div className="flex flex-col gap-1 text-sm">
      <p>Quote {preview.quoteId}</p>
      <p>Expires {preview.expiresAt}</p>
      {projection?.hasBalance ? (
        <p>
          Available {hoursText(projection.availableHours)}. Booked {hoursText(projection.bookedHours)}.
        </p>
      ) : (
        <p>This leave type has no balance.</p>
      )}
      {preview.warnings.length === 0 ? <p>No warning.</p> : null}
      {preview.warnings.map((warning) => (
        <p key={warning.code} className="text-red-700">
          {warning.message}
        </p>
      ))}
    </div>
  )
}

function ProjectionPanel({
  date,
  rows,
  error,
  pending,
}: {
  date: string
  rows: LeaveBalance[] | undefined
  error: unknown
  pending: boolean
}) {
  if (!date) {
    return <p className="text-sm">Hover or choose a date to see the balance.</p>
  }

  return (
    <section className="flex flex-col gap-1 border border-neutral-200 bg-white p-2">
      <h2 className="text-sm font-semibold">Balance on {date}</h2>
      {pending ? <p className="text-sm">Loading the balance.</p> : null}
      <ErrorText error={error} />
      {rows?.map((row) => (
        <p key={row.leaveTypeId} className="text-sm">
          {row.code}: {row.hasBalance ? `${hoursText(row.availableHours)} available, ${hoursText(row.bookedHours)} booked` : 'No balance'}
        </p>
      ))}
    </section>
  )
}
