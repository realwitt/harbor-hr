// Only return the browser to the Harbor authorize URL. Reject every other path.
export function safeAuthorizeNext(value: string): string | null {
  if (value.length === 0 || value.length > 4000) {
    return null
  }

  const path = '/connect/authorize'
  if (value !== path && !value.startsWith(`${path}?`)) {
    return null
  }

  if (value.includes('\\') || value.includes('\0') || value.includes('\n') || value.includes('\r')) {
    return null
  }

  return value
}
