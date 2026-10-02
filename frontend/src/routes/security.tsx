import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api'
import { ConnectGuide } from '../connect-guide'
import { meQuery } from '../query'
import type { McpClient, PasskeyRow } from '../types'
import { Button, ErrorText, Page } from '../ui'
import { createPasskey, stepUp } from '../webauthn'

export function SecurityPage() {
  const queryClient = useQueryClient()
  const me = useQuery(meQuery)
  const passkeys = useQuery({
    queryKey: ['passkeys'],
    queryFn: () => api<PasskeyRow[]>('/api/auth/passkeys'),
  })
  const clients = useQuery({
    queryKey: ['mcp-clients'],
    enabled: me.data?.ready === true,
    queryFn: () => api<McpClient[]>('/api/auth/mcp/clients'),
  })
  const refreshMe = async () => {
    await queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
    await queryClient.invalidateQueries({ queryKey: ['passkeys'] })
  }
  const addPasskey = useMutation({
    mutationFn: async () => {
      if (me.data?.ready) {
        await stepUp('enroll_passkey')
      }

      const options = await api<unknown>('/api/auth/passkeys/options', { method: 'POST' })
      const attestation = await createPasskey(options)
      await api('/api/auth/passkeys', {
        method: 'POST',
        body: JSON.stringify({ attestation }),
      })
    },
    onSuccess: refreshMe,
  })
  const removePasskey = useMutation({
    mutationFn: async (id: string) => {
      if (me.data?.ready) {
        await stepUp('remove_passkey')
      }

      await api(`/api/auth/passkeys/${id}`, { method: 'DELETE' })
    },
    onSuccess: refreshMe,
  })
  const generate = useMutation({
    mutationFn: async () => {
      if (me.data?.ready) {
        await stepUp('save_recovery')
      }

      return api<{ codes: string[] }>('/api/auth/recovery/generate', { method: 'POST' })
    },
  })
  const acknowledge = useMutation({
    mutationFn: () => api('/api/auth/recovery/acknowledge', { method: 'POST' }),
    onSuccess: async () => {
      generate.reset()
      await refreshMe()
    },
  })
  const enableMcp = useMutation({
    mutationFn: async () => {
      await stepUp('mcp_on')
      return api<{ mcpEnabledAt: string }>('/api/auth/mcp/enable', { method: 'POST' })
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
    },
  })
  const revoke = useMutation({
    mutationFn: async (id: string) => {
      await stepUp('revoke_client')
      await api(`/api/auth/mcp/clients/${id}/revoke`, { method: 'POST' })
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['mcp-clients'] })
    },
  })

  return (
    <Page title="Security">
      <ErrorText error={me.error ?? passkeys.error} />
      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">Passkeys</h2>
        {passkeys.data && passkeys.data.length === 0 ? <p className="text-sm">No passkey.</p> : null}
        {passkeys.data?.map((passkey) => (
          <div key={passkey.id} className="flex items-center gap-2 text-sm">
            <span>{passkey.nickname || 'Passkey'}</span>
            <span className="subtle">{passkey.createdAt}</span>
            <Button quiet onPress={() => removePasskey.mutate(passkey.id)} isDisabled={removePasskey.isPending}>
              Remove
            </Button>
          </div>
        ))}
        <Button onPress={() => addPasskey.mutate()} isDisabled={addPasskey.isPending}>
          Add passkey
        </Button>
        <ErrorText error={addPasskey.error ?? removePasskey.error} />
      </section>
      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">Recovery codes</h2>
        <p className="text-sm">Unused codes: {me.data?.unusedRecoveryCodeCount ?? 0}.</p>
        {generate.data ? (
          <>
            <p className="text-sm">Save these codes. Harbor shows them once.</p>
            <ul className="flex flex-col gap-0.5 text-sm">
              {generate.data.codes.map((code) => (
                <li key={code}>{code}</li>
              ))}
            </ul>
            <Button onPress={() => acknowledge.mutate()} isDisabled={acknowledge.isPending}>
              I saved these codes
            </Button>
          </>
        ) : (
          <Button onPress={() => generate.mutate()} isDisabled={generate.isPending}>
            Generate recovery codes
          </Button>
        )}
        <ErrorText error={generate.error ?? acknowledge.error} />
      </section>
      {me.data?.ready ? (
        <section className="flex flex-col gap-3">
          <h2 className="text-sm font-semibold">MCP clients</h2>
          {me.data.mcpEnabledAt ? (
            <p className="text-sm">MCP is on.</p>
          ) : (
            <>
              <p className="text-sm">Click Enable MCP before you connect an AI app.</p>
              <Button onPress={() => enableMcp.mutate()} isDisabled={enableMcp.isPending}>
                Enable MCP
              </Button>
            </>
          )}
          <ErrorText error={clients.error ?? enableMcp.error ?? revoke.error} />
          {clients.data && clients.data.length === 0 ? <p className="text-sm">No AI app is connected yet.</p> : null}
          {clients.data?.map((client) => (
            <div key={client.id} className="flex items-center gap-2 text-sm">
              <span>{client.clientName || 'Client'}</span>
              <span className="subtle">{client.revokedAt ? `Revoked ${client.revokedAt}` : client.createdAt}</span>
              {client.revokedAt ? null : (
                <Button quiet onPress={() => revoke.mutate(client.id)} isDisabled={revoke.isPending}>
                  Revoke
                </Button>
              )}
            </div>
          ))}
          <ConnectGuide />
        </section>
      ) : null}
    </Page>
  )
}
