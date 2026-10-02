import { Link } from '@tanstack/react-router'
import { useState } from 'react'
import { Button, Page } from '../ui'

export function ConnectAiPage() {
  const address = `${window.location.origin}/mcp`
  const command = `grok mcp add --transport http harbor ${address}`
  const config = `[mcp_servers.harbor]\nurl = "${address}"`
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
    <main className="guest">
      <div className="guest-card connect-card panel">
        <Page title="Connect with your AI">
          <p className="text-sm">Your AI can read your leave and submit leave for you.</p>
          <p className="text-sm">Your AI cannot approve leave.</p>
          <ol className="connect-steps">
            <li>Sign in to Harbor.</li>
            <li>Open Security and save your recovery codes.</li>
            <li>On Security, click Enable MCP. Confirm with your passkey.</li>
            <li>Add Harbor in your AI app. Use the address below.</li>
            <li>The app opens this browser. Sign in if Harbor asks.</li>
            <li>Click Accept. The app can then act as you.</li>
          </ol>
          <h2 className="text-sm font-semibold">Address</h2>
          <p className="connect-address">{address}</p>
          <Button onPress={() => void copy('Address copied.', address)}>Copy address</Button>
          <h2 className="text-sm font-semibold">Grok</h2>
          <p className="text-sm">Run this command, then start a new Grok session.</p>
          <pre className="connect-address">{command}</pre>
          <Button onPress={() => void copy('Command copied.', command)}>Copy command</Button>
          <p className="text-sm">Or add this block to ~/.grok/config.toml.</p>
          <pre className="connect-address">{config}</pre>
          <Button onPress={() => void copy('Config copied.', config)}>Copy config</Button>
          {copied ? <p className="text-sm">{copied}</p> : null}
          <p className="text-sm">In Claude, add a custom connector and paste the address.</p>
          <Link className="trouble-link" to="/sign-in" search={{ next: '' }}>
            Back to sign in
          </Link>
          <Link className="trouble-link" to="/security">
            Open Security
          </Link>
        </Page>
      </div>
    </main>
  )
}
