import { api } from './api'

export async function stepUp(action: string, quoteId?: string): Promise<void> {
  const options = await api<unknown>('/api/auth/step-up/options', {
    method: 'POST',
    body: JSON.stringify(quoteId ? { action, quoteId } : { action }),
  })
  const assertion = await getPasskey(options)
  await api('/api/auth/step-up', {
    method: 'POST',
    body: JSON.stringify({ assertion }),
  })
}

function passkeyMessage(error: unknown): string {
  if (error instanceof DOMException && error.name === 'NotAllowedError') {
    return 'The passkey check was cancelled.'
  }

  if (error instanceof Error && error.message) {
    return error.message
  }

  return 'The passkey check failed.'
}

export function asPasskeyError(error: unknown): Error {
  return new Error(passkeyMessage(error))
}

export async function createPasskey(options: unknown): Promise<unknown> {
  if (!PublicKeyCredential.parseCreationOptionsFromJSON) {
    throw new Error('This browser cannot create a passkey.')
  }

  const publicKey = PublicKeyCredential.parseCreationOptionsFromJSON(
    options as PublicKeyCredentialCreationOptionsJSON,
  )
  try {
    const credential = await navigator.credentials.create({ publicKey })
    if (!(credential instanceof PublicKeyCredential)) {
      throw new Error('The browser did not create a passkey.')
    }

    return credential.toJSON()
  } catch (error) {
    throw asPasskeyError(error)
  }
}

export async function getPasskey(options: unknown): Promise<unknown> {
  if (!PublicKeyCredential.parseRequestOptionsFromJSON) {
    throw new Error('This browser cannot use a passkey.')
  }

  const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(
    options as PublicKeyCredentialRequestOptionsJSON,
  )
  try {
    const credential = await navigator.credentials.get({ publicKey })
    if (!(credential instanceof PublicKeyCredential)) {
      throw new Error('The browser did not return a passkey.')
    }

    return credential.toJSON()
  } catch (error) {
    throw asPasskeyError(error)
  }
}
