import { useForm, useStore } from '@tanstack/react-form'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef } from 'react'
import { api, idempotencyHeaders } from '../api'
import { kindLabel, money, parseCents, statusLabel } from '../format'
import type { DeductionCommand, DeductionPreview, DeductionsResponse } from '../types'
import { useDebounced } from '../use-debounced'
import { Button, Choice, ErrorText, Page, TextField, fieldErrors } from '../ui'
import { stepUp } from '../webauthn'

const kinds = [
  { id: 'hsa', label: 'HSA' },
  { id: 'health_fsa', label: 'Health FSA' },
  { id: 'dependent_care_fsa', label: 'Dependent care FSA' },
]

export function DeductionsPage() {
  const current = useQuery({
    queryKey: ['deductions'],
    queryFn: () => api<DeductionsResponse>('/api/deductions'),
  })
  const active = (current.data?.elections ?? []).filter((row) => row.status === 'active' && row.endedOn == null)

  return (
    <Page title="Deductions">
      <ErrorText error={current.error} />
      <section className="flex flex-col gap-1">
        <h2 className="text-sm font-semibold">Current</h2>
        {active.length === 0 ? <p className="text-sm">No current deduction.</p> : null}
        {active.map((row) => (
          <p key={row.id} className="text-sm">
            {kindLabel(row.kind)} · {money(row.perPaycheckCents)} per paycheck · {statusLabel(row.status)}
          </p>
        ))}
      </section>
      <DeductionEditor />
    </Page>
  )
}

function DeductionEditor() {
  const queryClient = useQueryClient()
  const submitRef = useRef<() => void>(() => {})
  const form = useForm({
    defaultValues: {
      kind: 'hsa',
      amount: '',
      qualifyingEvent: '',
    },
    onSubmit: () => {
      submitRef.current()
    },
  })
  const values = useStore(form.store, (state) => state.values)
  const cents = parseCents(values.amount)
  const eventText = values.qualifyingEvent.trim()
  const debouncedCents = useDebounced(cents, 300)
  const debouncedEvent = useDebounced(eventText, 300)
  const ready = values.kind !== '' && debouncedCents !== null && debouncedCents === cents && debouncedEvent === eventText
  const preview = useQuery({
    queryKey: ['deduction-preview', values.kind, debouncedCents, debouncedEvent],
    enabled: values.kind !== '' && debouncedCents !== null,
    queryFn: () =>
      api<DeductionPreview>('/api/deductions/preview', {
        method: 'POST',
        body: JSON.stringify({
          kind: values.kind,
          perPaycheckCents: debouncedCents,
          qualifyingEvent: debouncedEvent || null,
        }),
      }),
  })
  const submit = useMutation({
    mutationFn: async () => {
      const quote = preview.data
      if (!quote || debouncedCents === null || !ready) {
        throw new Error('Wait for the preview.')
      }

      await stepUp('confirm_quote', quote.quoteId)
      return api<DeductionCommand>('/api/deductions/submit', {
        method: 'POST',
        headers: idempotencyHeaders(),
        body: JSON.stringify({
          quoteId: quote.quoteId,
          kind: values.kind,
          perPaycheckCents: debouncedCents,
          qualifyingEvent: debouncedEvent || null,
        }),
      })
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['deductions'] })
    },
  })
  submitRef.current = () => {
    if (ready && preview.data) {
      submit.mutate()
    }
  }

  return (
    <div className="grid gap-4 md:grid-cols-2">
      <form
        className="flex flex-col gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          void form.handleSubmit()
        }}
      >
        <form.Field name="kind">
          {(field) => <Choice label="Type" value={field.state.value} onChange={field.handleChange} options={kinds} />}
        </form.Field>
        <form.Field
          name="amount"
          validators={{
            onSubmit: ({ value }) => (parseCents(value) === null ? 'Enter an amount in dollars.' : undefined),
          }}
        >
          {(field) => (
            <TextField
              label="Amount per paycheck"
              value={field.state.value}
              onChange={field.handleChange}
              onBlur={field.handleBlur}
              error={fieldErrors(field.state.meta.errors)}
            />
          )}
        </form.Field>
        <form.Field name="qualifyingEvent">
          {(field) => (
            <TextField
              label="Qualifying event"
              value={field.state.value}
              onChange={field.handleChange}
              onBlur={field.handleBlur}
            />
          )}
        </form.Field>
        <Button type="submit" isDisabled={submit.isPending || !ready || !preview.data}>
          Submit
        </Button>
        {submit.data ? <p className="text-sm">Saved. Status: {statusLabel(submit.data.status)}.</p> : null}
        <ErrorText error={submit.error} />
      </form>
      <aside className="panel">
        <h2 className="text-sm font-semibold">Paycheck preview</h2>
        <p className="text-sm">This is an estimate. It is not a pay stub.</p>
        {preview.isFetching ? <p className="text-sm">Loading the estimate.</p> : null}
        <ErrorText error={preview.error} />
        {preview.data ? <EstimateView estimate={preview.data.estimate} /> : null}
      </aside>
    </div>
  )
}

function EstimateView({ estimate }: { estimate: DeductionPreview['estimate'] }) {
  return (
    <div className="flex flex-col gap-1 text-sm">
      <p>{estimate.label}</p>
      <p>Gross withheld: {money(estimate.grossWithheldCents)}</p>
      <p>Estimated federal tax saved: {money(estimate.estimatedFederalSavedCents)}</p>
      <p>Estimated FICA saved: {money(estimate.estimatedFicaSavedCents)}</p>
      <p>Estimated state tax saved: {money(estimate.estimatedStateSavedCents)}</p>
      <p>Estimated take-home reduction: {money(estimate.estimatedTakeHomeReductionCents)}</p>
    </div>
  )
}
