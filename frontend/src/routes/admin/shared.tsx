import { useQuery } from '@tanstack/react-query'
import { type ReactNode } from 'react'
import { api } from '../../api'
import type { AdminEmployee } from '../../types'
import { Button, ErrorText } from '../../ui'

export function useEmployees() {
  return useQuery({
    queryKey: ['admin-employees'],
    queryFn: () => api<AdminEmployee[]>('/api/admin/employees'),
  })
}

export function YearSwitch({ year, onChange }: { year: number; onChange: (year: number) => void }) {
  return (
    <div className="year-switch">
      <Button quiet onPress={() => onChange(year - 1)}>
        Previous
      </Button>
      <span className="year-label">{year}</span>
      <Button quiet onPress={() => onChange(year + 1)}>
        Next
      </Button>
    </div>
  )
}

export function FormShell({
  form,
  children,
  label = 'Save',
  onCancel,
}: {
  form: { handleSubmit: () => Promise<unknown> }
  children: ReactNode
  label?: string
  onCancel?: () => void
}) {
  const view = form as unknown as {
    Subscribe: (props: {
      selector: (state: { errorMap: { onSubmit?: unknown }; isSubmitting: boolean }) => unknown
      children: (value: unknown) => ReactNode
    }) => ReactNode
  }

  return (
    <form
      className="stack-form"
      onSubmit={(event) => {
        event.preventDefault()
        void form.handleSubmit()
      }}
    >
      {children}
      <view.Subscribe selector={(state) => state.errorMap.onSubmit}>
        {(error) => <ErrorText error={error} />}
      </view.Subscribe>
      <div className="row-actions">
        {onCancel ? (
          <Button quiet onPress={onCancel}>
            Cancel
          </Button>
        ) : null}
        <view.Subscribe selector={(state) => state.isSubmitting}>
          {(isSubmitting) => (
            <Button type="submit" isDisabled={Boolean(isSubmitting)}>
              {label}
            </Button>
          )}
        </view.Subscribe>
      </div>
    </form>
  )
}
