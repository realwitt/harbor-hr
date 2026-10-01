import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../api'
import { addDays, dayList, localIsoDate, statusLabel } from '../format'
import type { CalendarDay, LeaveType, QueueItem } from '../types'
import { Button, DateField, ErrorText, Page } from '../ui'

export function TeamPage() {
  const queryClient = useQueryClient()
  const [from, setFrom] = useState(localIsoDate)
  const [to, setTo] = useState(() => addDays(localIsoDate(), 14))
  const rangeOk = from !== '' && to !== '' && to >= from
  const types = useQuery({
    queryKey: ['leave-types'],
    queryFn: () => api<LeaveType[]>('/api/leave/types'),
  })
  const queue = useQuery({
    queryKey: ['team-queue'],
    queryFn: () => api<QueueItem[]>('/api/team/queue'),
  })
  const calendar = useQuery({
    queryKey: ['team-calendar', from, to],
    enabled: rangeOk,
    queryFn: () =>
      api<CalendarDay[]>(`/api/team/calendar?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`),
  })
  const decide = useMutation({
    mutationFn: (input: { id: string; action: 'approve' | 'deny' }) =>
      api(`/api/leave/requests/${input.id}/${input.action}`, { method: 'POST' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['team-queue'] })
      await queryClient.invalidateQueries({ queryKey: ['team-calendar'] })
    },
  })
  const names = new Map((types.data ?? []).map((type) => [type.id, type.code]))
  const days = (calendar.data ?? []).map((day) => ({
    ...day,
    id: `${day.requestId}-${day.on}`,
  }))

  return (
    <Page title="Team">
      <p className="text-sm">Direct reports only.</p>
      <ErrorText error={queue.error ?? types.error ?? decide.error} />
      <h2 className="text-sm font-semibold">Pending requests</h2>
      <Table aria-label="Pending requests" className="w-full text-sm">
        <TableHeader className="text-left text-xs text-neutral-500">
          <Column isRowHeader className="py-1 font-medium">
            Name
          </Column>
          <Column className="py-1 font-medium">Type</Column>
          <Column className="py-1 font-medium">Days</Column>
          <Column className="py-1 font-medium">Action</Column>
        </TableHeader>
        <TableBody items={queue.data ?? []} renderEmptyState={() => <p className="py-2 text-sm">No pending request.</p>}>
          {(item) => (
            <Row id={item.id} className="border-t border-neutral-200">
              <Cell className="py-1 pr-2">{item.employeeName}</Cell>
              <Cell className="py-1 pr-2">{names.get(item.leaveTypeId) ?? item.leaveTypeId}</Cell>
              <Cell className="py-1 pr-2">{dayList(item.days)}</Cell>
              <Cell className="flex gap-1 py-1">
                <Button onPress={() => decide.mutate({ id: item.id, action: 'approve' })} isDisabled={decide.isPending}>
                  Approve
                </Button>
                <Button quiet onPress={() => decide.mutate({ id: item.id, action: 'deny' })} isDisabled={decide.isPending}>
                  Deny
                </Button>
              </Cell>
            </Row>
          )}
        </TableBody>
      </Table>
      <h2 className="text-sm font-semibold">Team calendar</h2>
      <div className="flex flex-wrap gap-2">
        <DateField label="From" value={from} onChange={setFrom} />
        <DateField label="To" value={to} onChange={setTo} />
      </div>
      {!rangeOk ? <p className="text-sm">The end date is before the start date.</p> : null}
      <ErrorText error={calendar.error} />
      <Table aria-label="Team calendar" className="w-full text-sm">
        <TableHeader className="text-left text-xs text-neutral-500">
          <Column isRowHeader className="py-1 font-medium">
            Name
          </Column>
          <Column className="py-1 font-medium">Date</Column>
          <Column className="py-1 font-medium">Hours</Column>
          <Column className="py-1 font-medium">Type</Column>
          <Column className="py-1 font-medium">Status</Column>
        </TableHeader>
        <TableBody items={days} renderEmptyState={() => <p className="py-2 text-sm">No team day off in this range.</p>}>
          {(day) => (
            <Row id={day.id} className="border-t border-neutral-200">
              <Cell className="py-1 pr-2">{day.name}</Cell>
              <Cell className="py-1 pr-2">{day.on}</Cell>
              <Cell className="py-1 pr-2">{day.hours} h</Cell>
              <Cell className="py-1 pr-2">{names.get(day.leaveTypeId) ?? day.leaveTypeId}</Cell>
              <Cell className="py-1">{statusLabel(day.status)}</Cell>
            </Row>
          )}
        </TableBody>
      </Table>
    </Page>
  )
}
