export type Me = {
  employeeId: string
  email: string
  name: string
  role: 'employee' | 'hr_admin'
  ready: boolean
  passkeyCount: number
  unusedRecoveryCodeCount: number
}

export type LeaveType = {
  id: string
  code: string
  model: 'accrued' | 'instant' | 'unpaid'
  hoursPerGrant: number | null
  yearBoundary: string
  carryCapHours: number | null
  maxBalanceHours: number | null
  waitingDays: number
}

export type ProjectionEvent = {
  date: string
  kind: string
  hours: number
}

export type LeaveBalance = {
  leaveTypeId: string
  code: string
  model: 'accrued' | 'instant' | 'unpaid'
  hasBalance: boolean
  availableHours: number | null
  bookedHours: number | null
  events: ProjectionEvent[]
}

export type LeaveDay = {
  on: string
  hours: number
}

export type LeaveRequest = {
  id: string
  leaveTypeId: string
  status: 'pending' | 'approved' | 'denied' | 'cancelled'
  idempotencyKey: string
  quoteId: string
  adminOverride: boolean
  createdAt: string
  decidedAt: string | null
  decidedBy: string | null
  days: LeaveDay[]
}

export type LeaveWarning = {
  code: string
  message: string
}

export type LeaveProjection = {
  hasBalance: boolean
  availableHours: number | null
  bookedHours: number | null
  events: ProjectionEvent[]
}

export type LeavePreview = {
  quoteId: string
  expiresAt: string
  projection: LeaveProjection | null
  warnings: LeaveWarning[]
}

export type LeaveCommand = {
  requestId: string
  status: string
  replay: boolean
}

export type QueueItem = {
  id: string
  employeeId: string
  employeeName: string
  leaveTypeId: string
  status: string
  createdAt: string
  days: LeaveDay[]
}

export type CalendarDay = {
  employeeId: string
  name: string
  requestId: string
  leaveTypeId: string
  status: string
  on: string
  hours: number
}

export type DeductionElection = {
  id: string
  kind: string
  perPaycheckCents: number
  status: string
  effectiveOn: string
  endedOn: string | null
  qualifyingEvent: string | null
}

export type DeductionCap = {
  taxYear: number
  kind: string
  coverage: string
  limitCents: number
}

export type DeductionsResponse = {
  elections: DeductionElection[]
  caps: DeductionCap[]
}

export type PaycheckEstimate = {
  label: string
  isEstimate: boolean
  grossWithheldCents: number
  estimatedFederalSavedCents: number
  estimatedFicaSavedCents: number
  estimatedSocialSecuritySavedCents: number
  estimatedMedicareSavedCents: number
  estimatedStateSavedCents: number
  estimatedTakeHomeReductionCents: number
}

export type DeductionPreview = {
  quoteId: string
  expiresAt: string
  estimate: PaycheckEstimate
}

export type DeductionCommand = {
  electionId: string
  status: string
  replay: boolean
}

export type PasskeyRow = {
  id: string
  createdAt: string
  nickname: string | null
}

export type McpClient = {
  id: string
  clientName: string | null
  createdAt: string
  revokedAt: string | null
}

export type InviteInfo = {
  email: string
  name: string
}

export type AdminEmployee = {
  id: string
  name: string
  email: string
  role: 'employee' | 'hr_admin'
  hiredOn: string
  jurisdiction: string
  timezone: string
  hdhpEligible: boolean
  hsaCoverage: 'self' | 'family' | null
  terminatedOn: string | null
  managerId: string | null
  managerName: string | null
}

export type Blackout = {
  on: string
  reason: string
}

export type PayPeriod = {
  id: string
  startsOn: string
  endsOn: string
  payDate: string
}

export type Holiday = {
  on: string
  name: string
}

export type AuditRecord = {
  id: number
  recordId: string
  op: string
  ts: string
  tableSchema: string
  tableName: string
  record: unknown
  oldRecord: unknown
  changedFields: string[] | null
  actorId: string | null
  channel: string
  clientId: string | null
  authFactor: string
  requestId: string | null
  quoteId: string | null
  idempotencyKey: string | null
}

export type AuditAccess = {
  id: number
  at: string
  actorId: string | null
  action: string
  outcome: string
  channel: string
  clientId: string | null
  authFactor: string
  subjectId: string | null
  quoteId: string | null
  toolName: string | null
  requestId: string | null
  detail: unknown
}

export type AuditLog = {
  records: AuditRecord[]
  accessEvents: AuditAccess[]
}
