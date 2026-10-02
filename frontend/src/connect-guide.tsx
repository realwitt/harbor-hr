import { useState } from 'react'
import { Button } from './ui'

const grokConnectors = 'https://grok.com/connectors'
const grokClientId = 'grok'
const chatGptConnectors = 'https://chatgpt.com/#settings/Connectors'

export function ConnectGuide({ showPrep = false }: { showPrep?: boolean }) {
  const address = `${window.location.origin}/mcp`
  const command = `grok mcp add --transport http harbor ${address}`
  const [copied, setCopied] = useState('')

  async function copy(label: string, value: string) {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(label)
    } catch {
      setCopied('Copy failed.')
    }
  }

  return (
    <div className="connect-guide">
      <p className="text-sm">Your AI can read your leave and submit leave for you.</p>
      <p className="text-sm">Your AI cannot approve leave.</p>
      {showPrep ? (
        <ol className="connect-steps">
          <li>Sign in to Harbor.</li>
          <li>Open Security.</li>
          <li>Save your recovery codes if Harbor asks.</li>
          <li>Click Enable MCP. Confirm with your passkey.</li>
        </ol>
      ) : null}
      <h2 className="text-sm font-semibold">Address</h2>
      <p className="text-sm">Paste this address.</p>
      <p className="connect-address">{address}</p>
      <Button onPress={() => void copy('Address copied.', address)}>Copy address</Button>
      <section className="connect-app">
        <h2 className="text-sm font-semibold">Grok</h2>
        <p className="text-sm">Use grok.com. The Grok phone app cannot add this connector.</p>
        <ol className="connect-steps">
          <li>
            Open <a href={grokConnectors} target="_blank" rel="noreferrer">Grok connectors</a>.
          </li>
          <li>Click New Connector, then Custom.</li>
          <li>Name it Harbor.</li>
          <li>Paste the address. Click Add Connector.</li>
          <li>If Grok asks for a client id, paste grok. Leave the client secret empty.</li>
          <li>Leave scopes empty. Set token auth method to none.</li>
          <li>Click Save and connect.</li>
          <li>Sign in with your passkey. Click Accept.</li>
        </ol>
        <p className="connect-address">{grokClientId}</p>
        <Button quiet onPress={() => void copy('Client id copied.', grokClientId)}>
          Copy client id
        </Button>
        <p className="text-sm">If you do not see Custom, ask a team admin to add the connector.</p>
      </section>
      <section className="connect-app">
        <h2 className="text-sm font-semibold">Claude</h2>
        <ol className="connect-steps">
          <li>Open Customize, then Connectors.</li>
          <li>Click Add custom connector.</li>
          <li>Name it Harbor.</li>
          <li>Paste the address.</li>
          <li>Do not type a client id or a client secret.</li>
          <li>Click Connect. On the Harbor page, click Accept.</li>
          <li>In a chat, turn Harbor on.</li>
        </ol>
      </section>
      <section className="connect-app">
        <h2 className="text-sm font-semibold">ChatGPT</h2>
        <ol className="connect-steps">
          <li>
            Open <a href={chatGptConnectors} target="_blank" rel="noreferrer">ChatGPT connectors</a>.
          </li>
          <li>Open Advanced settings.</li>
          <li>Turn Developer Mode on.</li>
          <li>Click Create.</li>
          <li>Name it Harbor.</li>
          <li>Paste the address. Set authentication to OAuth.</li>
          <li>Click Create. On the Harbor page, click Accept.</li>
          <li>In a new chat, turn Developer Mode on and select Harbor.</li>
        </ol>
      </section>
      <section className="connect-app">
        <h2 className="text-sm font-semibold">Grok on this computer</h2>
        <p className="text-sm">Run this command. Then start a new Grok session.</p>
        <pre className="connect-address">{command}</pre>
        <Button quiet onPress={() => void copy('Command copied.', command)}>
          Copy command
        </Button>
      </section>
      <p className="text-sm">After you click Accept, Security lists the app under MCP clients.</p>
      {copied ? (
        <p className="text-sm" role="status">
          {copied}
        </p>
      ) : null}
    </div>
  )
}
