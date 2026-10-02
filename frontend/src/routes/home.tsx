import { useQuery } from '@tanstack/react-query'
import { api } from '../api'
import { dayCount, hoursText, leaveTypeLabel, localIsoDate } from '../format'
import type { LeaveBalance, LeaveType } from '../types'
import { ErrorText, Page } from '../ui'

const paychecksPerYear = 26

function yearHours(type: LeaveType): number | null {
  if (type.hoursPerGrant == null) {
    return null
  }

  if (type.model === 'accrued') {
    return type.hoursPerGrant * paychecksPerYear
  }

  if (type.model === 'instant') {
    return type.hoursPerGrant
  }

  return null
}

export function HomePage() {
  const today = localIsoDate()
  const balances = useQuery({
    queryKey: ['leave-balances', today],
    queryFn: () => api<LeaveBalance[]>(`/api/leave/balances?on=${encodeURIComponent(today)}`),
  })
  const types = useQuery({
    queryKey: ['leave-types'],
    queryFn: () => api<LeaveType[]>('/api/leave/types'),
  })
  const ready = balances.data != null && types.data != null
  const typeById = new Map((types.data ?? []).map((type) => [type.id, type]))
  const cards = (ready ? balances.data ?? [] : [])
    .filter((row) => row.hasBalance)
    .map((row) => ({ row, type: typeById.get(row.leaveTypeId) }))
    .sort((left, right) => {
      const leftRank = left.row.code === 'pto' ? 0 : 1
      const rightRank = right.row.code === 'pto' ? 0 : 1
      return leftRank - rightRank || left.row.code.localeCompare(right.row.code)
    })

  return (
    <Page title="Home">
      <ErrorText error={balances.error ?? types.error} />
      {balances.isPending || types.isPending ? <p>Loading balances.</p> : null}
      {ready && cards.length === 0 ? <p>No leave balance.</p> : null}
      {cards.length > 0 ? (
        <div className="home-stats">
          {cards.map(({ row, type }) => {
            const yearly = type ? yearHours(type) : null
            return (
              <article key={row.leaveTypeId} className="panel">
                <h2 className="stat-code">{leaveTypeLabel(row.code)}</h2>
                <div className="stat-grid">
                  <div>
                    <p className="stat-value">{hoursText(row.availableHours)}</p>
                    <p className="stat-meta">Balance</p>
                  </div>
                  <div>
                    <p className="stat-value">{type ? hoursText(type.hoursPerGrant) : '—'}</p>
                    <p className="stat-meta">{type?.model === 'accrued' ? 'Per paycheck' : 'Each year'}</p>
                  </div>
                  <div>
                    <p className="stat-value">{yearly == null ? '—' : dayCount(yearly)}</p>
                    <p className="stat-meta">Days per year</p>
                  </div>
                </div>
              </article>
            )
          })}
        </div>
      ) : null}
    </Page>
  )
}
