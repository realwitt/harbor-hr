import { CalendarDate, getLocalTimeZone, today } from '@internationalized/date'

export function localIsoDate(): string {
  return today(getLocalTimeZone()).toString()
}

export function addDays(iso: string, days: number): string {
  return new CalendarDate(
    Number(iso.slice(0, 4)),
    Number(iso.slice(5, 7)),
    Number(iso.slice(8, 10)),
  )
    .add({ days })
    .toString()
}

export function money(cents: number): string {
  const sign = cents < 0 ? '-' : ''
  const abs = Math.abs(cents)
  const dollars = Math.floor(abs / 100)
  const rem = String(abs % 100).padStart(2, '0')
  return `${sign}$${dollars.toLocaleString('en-US')}.${rem}`
}

export function parseCents(text: string): number | null {
  const trimmed = text.trim()
  if (!/^\d+(\.\d{1,2})?$/.test(trimmed)) {
    return null
  }

  const [dollars, fraction = ''] = trimmed.split('.')
  const cents = Number(dollars) * 100 + Number(fraction.padEnd(2, '0'))
  return Number.isSafeInteger(cents) ? cents : null
}

export function parseHours(text: string, allowNegative: boolean): number | null {
  const trimmed = text.trim()
  const pattern = allowNegative ? /^-?\d+(\.\d{1,2})?$/ : /^\d+(\.\d{1,2})?$/
  if (!pattern.test(trimmed)) {
    return null
  }

  const value = Number(trimmed)
  return Number.isFinite(value) ? value : null
}

export function hoursText(value: number | null | undefined): string {
  if (value === null || value === undefined) {
    return '—'
  }

  return `${value} h`
}

const hoursPerWorkDay = 8

export function dayCount(hours: number): string {
  const days = Math.round((hours / hoursPerWorkDay) * 100) / 100
  return days.toFixed(2).replace(/\.?0+$/, '')
}

export function daysText(hours: number): string {
  const text = dayCount(hours)
  return `${text} ${text === '1' ? 'day' : 'days'}`
}

export function leaveTypeLabel(code: string): string {
  switch (code) {
    case 'flex':
      return 'Flex'
    case 'jury':
      return 'Jury'
    case 'pto':
      return 'PTO'
    case 'unpaid':
      return 'Unpaid'
    default:
      return code
  }
}

export function monthDay(iso: string): string {
  return dateText(iso, { month: 'short', day: 'numeric' })
}

export function mediumDate(iso: string): string {
  return dateText(iso, { month: 'short', day: 'numeric', year: 'numeric' })
}

export function dateRange(start: string, end: string): string {
  if (start.slice(0, 4) === end.slice(0, 4)) {
    return `${monthDay(start)} – ${monthDay(end)}`
  }

  return `${mediumDate(start)} – ${mediumDate(end)}`
}

export function shortStamp(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(date)
}

function dateText(iso: string, options: Intl.DateTimeFormatOptions): string {
  const year = Number(iso.slice(0, 4))
  const month = Number(iso.slice(5, 7))
  const day = Number(iso.slice(8, 10))
  if (!year || !month || !day) {
    return iso
  }

  return new Intl.DateTimeFormat('en-US', { ...options, timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, day)))
}

export function roleLabel(role: string): string {
  return role === 'hr_admin' ? 'HR admin' : 'Employee'
}

export function planLabel(eligible: boolean, coverage: string | null): string {
  if (!eligible) {
    return 'No HDHP'
  }

  if (coverage === 'family') {
    return 'HDHP · Family'
  }

  if (coverage === 'self') {
    return 'HDHP · Self'
  }

  return 'HDHP'
}

export function coverageLabel(coverage: string): string {
  switch (coverage) {
    case 'self':
      return 'Self'
    case 'family':
      return 'Family'
    case 'catch_up':
      return 'Catch-up'
    case 'employee':
      return 'Employee'
    default:
      return coverage
  }
}

export function kindLabel(kind: string): string {
  switch (kind) {
    case 'hsa':
      return 'HSA'
    case 'health_fsa':
      return 'Health FSA'
    case 'dependent_care_fsa':
      return 'Dependent care FSA'
    default:
      return kind
  }
}

export function statusLabel(status: string): string {
  return status.replaceAll('_', ' ')
}

export function daySpan(days: { on: string }[]): string {
  if (days.length === 0) {
    return 'No date'
  }

  const first = days[0]?.on ?? ''
  const last = days[days.length - 1]?.on ?? first
  return first === last ? first : `${first} – ${last}`
}

export function dayList(days: { on: string; hours: number }[]): string {
  if (days.length === 0) {
    return 'No date'
  }

  return days.map((day) => `${day.on} (${day.hours} h)`).join(', ')
}

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function isGuid(value: string): boolean {
  return guidPattern.test(value.trim())
}
