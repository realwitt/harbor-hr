import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../../api'
import { shortStamp, statusLabel } from '../../format'
import type { AuditLog } from '../../types'
import { Button, Choice, DateField, ErrorText, Page, TextField } from '../../ui'
import { stepUp } from '../../webauthn'
import { useEmployees } from './shared'

const channels = [
  { id: 'any', label: 'Any channel' },
  { id: 'web', label: 'Web' },
  { id: 'mcp', label: 'MCP' },
  { id: 'job', label: 'Job' },
  { id: 'system', label: 'System' },
]

export function AuditPage() {
  const queryClient = useQueryClient()
  const people = useEmployees()
  const [filters, setFilters] = useState({ actorId: 'any', table: '', channel: 'any', from: '', to: '' })
  const [armed, setArmed] = useState(false)
  const names = new Map((people.data ?? []).map((person) => [person.id, person.name]))
  const read = useMutation({
    mutationFn: () => stepUp('read_audit'),
    onSuccess: async () => {
      setArmed(true)
      await queryClient.invalidateQueries({ queryKey: ['audit'] })
    },
  })
  const audit = useQuery({
    queryKey: ['audit', filters],
    enabled: armed,
    queryFn: () => {
      const params = new URLSearchParams()
      if (filters.actorId && filters.actorId !== 'any') {
        params.set('actorId', filters.actorId)
      }

      if (filters.table.trim()) {
        params.set('table', filters.table.trim())
      }

      if (filters.channel && filters.channel !== 'any') {
        params.set('channel', filters.channel)
      }

      if (filters.from) {
        params.set('from', filters.from)
      }

      if (filters.to) {
        params.set('to', filters.to)
      }

      const query = params.toString()
      return api<AuditLog>(`/api/admin/audit${query ? `?${query}` : ''}`)
    },
  })
  const records = (audit.data?.records ?? []).map((record) => ({ ...record, id: String(record.id) }))
  const access = (audit.data?.accessEvents ?? []).map((event) => ({ ...event, id: String(event.id) }))
  const actors = [
    { id: 'any', label: 'Any person' },
    ...(people.data ?? []).map((person) => ({ id: person.id, label: person.name })),
  ]

  return (
    <Page title="Audit log">
      <p className="subtle">Read the audit log after a passkey check.</p>
      <div className="form-grid">
        <Choice label="Person" value={filters.actorId} onChange={(actorId) => setFilters({ ...filters, actorId })} options={actors} />
        <TextField label="Table" value={filters.table} onChange={(table) => setFilters({ ...filters, table })} />
        <Choice label="Channel" value={filters.channel} onChange={(channel) => setFilters({ ...filters, channel })} options={channels} />
        <DateField label="From" value={filters.from} onChange={(from) => setFilters({ ...filters, from })} />
        <DateField label="To" value={filters.to} onChange={(to) => setFilters({ ...filters, to })} />
      </div>
      <div className="row-actions">
        <Button onPress={() => read.mutate()} isDisabled={read.isPending}>
          Read audit
        </Button>
      </div>
      <ErrorText error={people.error ?? read.error ?? audit.error} />
      {armed ? (
        <>
          <h2 className="section-title">Records</h2>
          <div className="table-wrap">
            <Table aria-label="Audit records">
              <TableHeader>
                <Column isRowHeader>Time</Column>
                <Column>Table</Column>
                <Column>Op</Column>
                <Column>Person</Column>
                <Column>Channel</Column>
              </TableHeader>
              <TableBody items={records} renderEmptyState={() => <p className="empty-row">No audit row.</p>}>
                {(record) => (
                  <Row id={record.id}>
                    <Cell>{shortStamp(record.ts)}</Cell>
                    <Cell>
                      {record.tableSchema}.{record.tableName}
                    </Cell>
                    <Cell>{record.op}</Cell>
                    <Cell>{record.actorId ? (names.get(record.actorId) ?? 'Unknown') : 'System'}</Cell>
                    <Cell>{statusLabel(record.channel)}</Cell>
                  </Row>
                )}
              </TableBody>
            </Table>
          </div>
          <h2 className="section-title">Access</h2>
          <div className="table-wrap">
            <Table aria-label="Access events">
              <TableHeader>
                <Column isRowHeader>Time</Column>
                <Column>Action</Column>
                <Column>Result</Column>
                <Column>Person</Column>
                <Column>Channel</Column>
              </TableHeader>
              <TableBody items={access} renderEmptyState={() => <p className="empty-row">No access event.</p>}>
                {(event) => (
                  <Row id={event.id}>
                    <Cell>{shortStamp(event.at)}</Cell>
                    <Cell>{statusLabel(event.action)}</Cell>
                    <Cell>{statusLabel(event.outcome)}</Cell>
                    <Cell>{event.actorId ? (names.get(event.actorId) ?? 'Unknown') : 'System'}</Cell>
                    <Cell>{statusLabel(event.channel)}</Cell>
                  </Row>
                )}
              </TableBody>
            </Table>
          </div>
        </>
      ) : null}
    </Page>
  )
}
