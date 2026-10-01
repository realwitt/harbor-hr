import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api'
import { hoursText, kindLabel, localIsoDate, money } from '../format'
import type { DeductionsResponse, LeaveBalance, LeaveRequest, LeaveType } from '../types'
import { DateField, ErrorText, Page } from '../ui'

export function HomePage() {
  const [on, setOn] = useState(localIsoDate)
  const balances = useQuery({
    queryKey: ['leave-balances', on],
    queryFn: () => api<LeaveBalance[]>(`/api/leave/balances?on=${encodeURIComponent(on)}`),
  })
  const requests = useQuery({
    queryKey: ['leave-requests'],
    queryFn: () => api<LeaveRequest[]>('/api/leave/requests'),
  })
  const types = useQuery({
    queryKey: ['leave-types'],
    queryFn: () => api<LeaveType[]>('/api/leave/types'),
  })
  const deductions = useQuery({
    queryKey: ['deductions'],
    queryFn: () => api<DeductionsResponse>('/api/deductions'),
  })
  const typeName = new Map((types.data ?? []).map((type) => [type.id, type.code]))
  const nextDay = (requests.data ?? [])
    .filter((request) => request.status === 'approved')
    .flatMap((request) => request.days.map((day) => ({ ...day, leaveTypeId: request.leaveTypeId })))
    .filter((day) => day.on >= on)
    .sort((left, right) => left.on.localeCompare(right.on))[0]
  const current = (deductions.data?.elections ?? []).filter(
    (election) => election.status === 'active' && election.endedOn == null,
  )

  return (
    <Page title="Home">
      <DateField label="On" value={on} onChange={setOn} />
      <ErrorText error={balances.error ?? requests.error ?? deductions.error} />
      <section className="flex flex-col gap-1">
        <h2 className="text-sm font-semibold">Balances</h2>
        {balances.data && balances.data.length === 0 ? <p className="text-sm">No leave type is set up.</p> : null}
        {balances.data?.map((row) => (
          <p key={row.leaveTypeId} className="text-sm">
            {row.code}: {row.hasBalance ? `${hoursText(row.availableHours)} available, ${hoursText(row.bookedHours)} booked` : 'No balance'}
          </p>
        ))}
      </section>
      <section className="flex flex-col gap-1">
        <h2 className="text-sm font-semibold">Next approved day off</h2>
        {nextDay ? (
          <p className="text-sm">
            {nextDay.on} · {typeName.get(nextDay.leaveTypeId) ?? 'Leave'} · {nextDay.hours} h
          </p>
        ) : (
          <p className="text-sm">No approved day off.</p>
        )}
      </section>
      <section className="flex flex-col gap-1">
        <h2 className="text-sm font-semibold">Current deductions</h2>
        {current.length === 0 ? <p className="text-sm">No current deduction.</p> : null}
        {current.map((election) => (
          <p key={election.id} className="text-sm">
            {kindLabel(election.kind)} · {money(election.perPaycheckCents)} per paycheck
          </p>
        ))}
      </section>
    </Page>
  )
}
