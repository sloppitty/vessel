import { pairingSecretPattern } from '@/api/auth'

const prefix = '#pair='

/** The link the desktop's QR carries. The secret stays in the fragment: it is never sent in a request line or `Referer`. */
export const pairingUrl = (origin: string, secret: string) => `${origin}/${prefix}${secret}`

/**
 * Reads a `#pair=` fragment and removes it from the address at once, so the secret never stays in the URL, the
 * history entry or a bookmark. Returns null (and leaves the address alone) for any other fragment.
 */
export function takePairingSecret(location: Pick<Location, 'hash' | 'pathname' | 'search'> = window.location,
  history: Pick<History, 'replaceState' | 'state'> = window.history): string | null {
  if (!location.hash.startsWith(prefix)) return null
  const secret = location.hash.slice(prefix.length)
  try { history.replaceState(history.state, '', `${location.pathname}${location.search}`) } catch { /* The secret was read; the address keeps it at worst. */ }
  return pairingSecretPattern.test(secret) ? secret : null
}

export type ScanResult =
  /** A pairing link for this Vessel. */
  | { kind: 'secret'; secret: string }
  /** A pairing link for another origin: refused, and scanning goes on. */
  | { kind: 'foreign' }
  /** Anything else a camera may read (a shop's QR code): ignored. */
  | { kind: 'ignore' }

/** Accepts only a URL on `origin` with a `#pair=` fragment; a pairing link for another origin is `foreign`. */
export function classifyScan(text: string, origin: string): ScanResult {
  let url: URL
  try { url = new URL(text) } catch { return { kind: 'ignore' } }
  if (!url.hash.startsWith(prefix)) return { kind: 'ignore' }
  if (url.origin !== origin) return { kind: 'foreign' }
  const secret = url.hash.slice(prefix.length)
  return pairingSecretPattern.test(secret) ? { kind: 'secret', secret } : { kind: 'ignore' }
}

/** A secure context with `getUserMedia`: not chosen by user agent. A plain-HTTP Development origin has no camera. */
export function cameraUsable(): boolean {
  return typeof window !== 'undefined' && window.isSecureContext && typeof navigator.mediaDevices?.getUserMedia === 'function'
}

/** A phone cannot open a loopback address, so the desktop shows no QR for it. */
export function isLoopbackOrigin(origin: string): boolean {
  try {
    const host = new URL(origin).hostname
    return host === 'localhost' || host.endsWith('.localhost') || host === '[::1]' || host === '::1' || /^127(\.\d{1,3}){3}$/.test(host)
  } catch { return false }
}

/** `482913` as `482 913`. */
export const groupedCode = (code: string) => `${code.slice(0, 3)} ${code.slice(3)}`
