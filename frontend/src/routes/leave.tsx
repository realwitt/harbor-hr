import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api, idempotencyHeaders } from '../api'
import { dayList, statusLabel } from '../format'
import type { LeavePreview, LeaveRequest, LeaveType } from '../types'
import { Button, ErrorText, Page, Prompt } from '../ui'
import { stepUp } from '../webauthn'
import { hoursText } from '../format'

export function LeavePage() {
  const requests = useQuery({
    queryKey: ['leave-requests'],
    queryFn: () => api<LeaveRequest[]>('/api/leave/requests'),
  })
  const types = useQuery({
    queryKey: ['leave-types'],
    queryFn: () => api<LeaveType[]>('/api/leave/types'),
  })
  const names = new Map((types.data ?? []).map((type) => [type.id, type.code]))

  return (
    <Page title="My requests" action={<Button to="/leave/new">Create request</Button>}>
      <ErrorText error={requests.error ?? types.error} />
      <Table aria-label="My requests">
        <TableHeader>
          <Column isRowHeader>Type</Column>
          <Column>Status</Column>
          <Column>Days</Column>
          <Column>Action</Column>
        </TableHeader>
        <TableBody
          items={requests.data ?? []}
          renderEmptyState={() => <p className="py-2 text-sm">No leave request.</p>}
        >
          {(request) => (
            <Row id={request.id}>
              <Cell>{names.get(request.leaveTypeId) ?? request.leaveTypeId}</Cell>
              <Cell>{statusLabel(request.status)}</Cell>
              <Cell>{dayList(request.days)}</Cell>
              <Cell>
                {request.status === 'pending' ? <CancelRequest requestId={request.id} /> : null}
              </Cell>
            </Row>
          )}
        </TableBody>
      </Table>
    </Page>
  )
}

function CancelRequest({ requestId }: { requestId: string }) {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const preview = useMutation({
    mutationFn: () =>
      api<LeavePreview>('/api/leave/cancel/preview', {
        method: 'POST',
        body: JSON.stringify({ requestId }),
      }),
    onSuccess: () => setOpen(true),
  })
  const cancel = useMutation({
    mutationFn: async (quote: LeavePreview) => {
      await stepUp('confirm_quote', quote.quoteId)
      await api('/api/leave/cancel', {
        method: 'POST',
        headers: idempotencyHeaders(),
        body: JSON.stringify({ quoteId: quote.quoteId, requestId }),
      })
    },
    onSuccess: async () => {
      setOpen(false)
      await queryClient.invalidateQueries({ queryKey: ['leave-requests'] })
      await queryClient.invalidateQueries({ queryKey: ['leave-balances'] })
    },
  })

  return (
    <>
      <Button onPress={() => preview.mutate()} isDisabled={preview.isPending}>
        Cancel
      </Button>
      <ErrorText error={preview.error} />
      <Prompt open={open} onOpenChange={setOpen} title="Cancel request">
        {preview.data ? (
          <>
            {preview.data.projection?.hasBalance ? (
              <p className="text-sm">
                Available {hoursText(preview.data.projection.availableHours)}. Booked{' '}
                {hoursText(preview.data.projection.bookedHours)}.
              </p>
            ) : (
              <p className="text-sm">This leave type has no balance.</p>
            )}
            {preview.data.warnings.map((warning) => (
              <p key={warning.code} className="invalid">
                {warning.message}
              </p>
            ))}
            <Button
              onPress={() => {
                if (preview.data) {
                  cancel.mutate(preview.data)
                }
              }}
              isDisabled={cancel.isPending}
            >
              Confirm cancel
            </Button>
            <ErrorText error={cancel.error} />
          </>
        ) : null}
      </Prompt>
    </>
  )
}
