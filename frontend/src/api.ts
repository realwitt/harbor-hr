export type ApiFailure = {
  code: string
  message: string
}

const authText: Record<string, string> = {
  sign_in_required: 'Sign in is required.',
  invite_invalid: 'This invite is not valid.',
  invalid: 'The request is not valid.',
  step_up_required: 'Confirm with your passkey first.',
  assertion_failed: 'The passkey check failed.',
  challenge_missing: 'The passkey check expired. Try again.',
  no_passkey: 'Add a passkey first.',
  invalid_action: 'That action is not valid.',
  quote_required: 'A quote is required.',
  quote_invalid: 'The quote is not valid. Preview again.',
  quote_missing: 'The quote is missing.',
  quote_expired: 'The quote is expired. Preview again.',
  quote_unconfirmed: 'The quote is not confirmed.',
  quote_mismatch: 'The quote does not match this request.',
  quote_consumed: 'The quote is already used.',
  invalid_code: 'The recovery code is not valid.',
  recovery_not_saved: 'Save the recovery codes first.',
  last_passkey: 'The last passkey stays.',
  not_authorized: 'You cannot do that.',
  account_not_ready: 'Finish security setup first.',
  conflict: 'That item already exists.',
  not_found: 'That item is missing.',
  account_exists: 'An account with this email already exists. Sign in.',
  request_pending: 'A request for this email is already sent.',
  not_pending: 'This request is already closed.',
  manager_missing: 'That manager is missing.',
  turnstile_required: 'A browser check is required.',
  turnstile_failed: 'The browser check failed.',
}

export class ApiError extends Error {
  readonly status: number
  readonly errors: ApiFailure[]

  constructor(status: number, errors: ApiFailure[]) {
    super(errors.map((item) => item.message).join(' '))
    this.name = 'ApiError'
    this.status = status
    this.errors = errors
  }
}

function readErrors(body: unknown): ApiFailure[] {
  if (body && typeof body === 'object' && 'errors' in body) {
    const errors = (body as { errors: unknown }).errors
    if (Array.isArray(errors)) {
      return errors.map((item) => {
        const row = item as { code?: string; message?: string }
        const code = row.code ?? 'error'
        const message = row.message || authText[code] || code
        return { code, message }
      })
    }
  }

  if (body && typeof body === 'object' && 'error' in body) {
    const code = (body as { error: unknown }).error
    if (typeof code === 'string') {
      return [{ code, message: authText[code] ?? code }]
    }
  }

  return [{ code: 'error', message: 'The request failed.' }]
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers)
  if (init?.body && !headers.has('content-type')) {
    headers.set('content-type', 'application/json')
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'include',
  })

  if (response.status === 204) {
    return undefined as T
  }

  const text = await response.text()
  let body: unknown = null
  if (text) {
    try {
      body = JSON.parse(text) as unknown
    } catch {
      body = null
    }
  }

  if (!response.ok) {
    throw new ApiError(response.status, readErrors(body))
  }

  return body as T
}

export function idempotencyHeaders(): HeadersInit {
  return { 'Idempotency-Key': crypto.randomUUID() }
}
