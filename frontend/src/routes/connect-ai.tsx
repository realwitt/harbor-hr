import { Link } from '@tanstack/react-router'
import { ConnectGuide } from '../connect-guide'
import { Page } from '../ui'

export function ConnectAiPage() {
  return (
    <main className="guest guest-start">
      <div className="guest-card connect-card panel">
        <Page title="Connect with your AI">
          <ConnectGuide showPrep />
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
