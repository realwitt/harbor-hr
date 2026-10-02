const joinReturn = /^\/admin\/join-requests\/([0-9a-fA-F-]{36})$/

export function safeJoinReturn(value: string): string {
  return joinReturn.test(value) ? value : ''
}

export function joinReturnId(value: string): string | null {
  return joinReturn.exec(value)?.[1] ?? null
}
