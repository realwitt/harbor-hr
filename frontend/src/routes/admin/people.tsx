import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Cell, Column, Row, Table, TableBody, TableHeader } from 'react-aria-components'
import { api } from '../../api'
import { mediumDate, planLabel, roleLabel } from '../../format'
import type { AdminEmployee } from '../../types'
import { Button, Check, Choice, ErrorText, Page, Prompt, TextField } from '../../ui'
import { useEmployees } from './shared'

const coverageChoices = [
  { id: 'none', label: 'No HSA' },
  { id: 'self', label: 'Self' },
  { id: 'family', label: 'Family' },
]

export function PeoplePage() {
  const people = useEmployees()
  const [search, setSearch] = useState('')
  const [edit, setEdit] = useState<AdminEmployee | null>(null)
  const query = search.trim().toLowerCase()
  const rows = useMemo(() => {
    return (people.data ?? []).filter((person) => {
      if (!query) {
        return true
      }

      return `${person.name} ${person.email} ${person.managerName ?? ''}`.toLowerCase().includes(query)
    })
  }, [people.data, query])

  return (
    <Page title="Directory">
      <div className="toolbar">
        <p className="subtle">Set the health plan for a person.</p>
        <TextField label="Search" value={search} onChange={setSearch} />
      </div>
      <ErrorText error={people.error} />
      <div className="table-wrap">
        <Table aria-label="People">
          <TableHeader>
            <Column isRowHeader>Name</Column>
            <Column>Email</Column>
            <Column>Role</Column>
            <Column>Manager</Column>
            <Column>Hired</Column>
            <Column>Plan</Column>
            <Column>Action</Column>
          </TableHeader>
          <TableBody items={rows} renderEmptyState={() => <p className="empty-row">No person matches.</p>}>
            {(person) => (
              <Row id={person.id}>
                <Cell>{person.name}</Cell>
                <Cell>{person.email}</Cell>
                <Cell>{roleLabel(person.role)}</Cell>
                <Cell>{person.managerName ?? '—'}</Cell>
                <Cell>{mediumDate(person.hiredOn)}</Cell>
                <Cell>{planLabel(person.hdhpEligible, person.hsaCoverage)}</Cell>
                <Cell>
                  <Button quiet onPress={() => setEdit(person)}>
                    Edit plan
                  </Button>
                </Cell>
              </Row>
            )}
          </TableBody>
        </Table>
      </div>
      <PlanDialog person={edit} onClose={() => setEdit(null)} />
    </Page>
  )
}

function PlanDialog({ person, onClose }: { person: AdminEmployee | null; onClose: () => void }) {
  return (
    <Prompt
      open={person !== null}
      onOpenChange={(open) => {
        if (!open) {
          onClose()
        }
      }}
      title={person ? `Health plan for ${person.name}` : 'Health plan'}
    >
      {person ? <PlanForm key={person.id} person={person} onClose={onClose} /> : null}
    </Prompt>
  )
}

function PlanForm({ person, onClose }: { person: AdminEmployee; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [eligible, setEligible] = useState(person.hdhpEligible)
  const [coverage, setCoverage] = useState(person.hsaCoverage ?? 'none')
  const save = useMutation({
    mutationFn: () =>
      api(`/api/admin/employees/${person.id}/hdhp`, {
        method: 'POST',
        body: JSON.stringify({
          hdhpEligible: eligible,
          hsaCoverage: eligible && coverage !== 'none' ? coverage : null,
        }),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin-employees'] })
      onClose()
    },
  })

  return (
    <form
      className="stack-form"
      onSubmit={(event) => {
        event.preventDefault()
        save.mutate()
      }}
    >
      <Check label="HDHP eligible" isSelected={eligible} onChange={setEligible} />
      {eligible ? <Choice label="HSA coverage" value={coverage} onChange={setCoverage} options={coverageChoices} /> : null}
      <ErrorText error={save.error} />
      <div className="row-actions">
        <Button quiet onPress={onClose}>
          Cancel
        </Button>
        <Button type="submit" isDisabled={save.isPending}>
          Save plan
        </Button>
      </div>
    </form>
  )
}
