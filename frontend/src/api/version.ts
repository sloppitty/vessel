import { isRecord } from './guards'

/** Set by Vite from `package.json` for production builds; the dev server reports a placeholder. */
declare const __VESSEL_VERSION__: string | undefined

/** The version this page was built as. Read at call time, so tests can stub the global. */
export function webVersion(): string {
  return typeof __VESSEL_VERSION__ === 'string' ? __VESSEL_VERSION__ : 'development'
}

const release = /^\d{1,6}\.\d{1,6}\.\d{1,6}$/

/** Development builds and unversioned assemblies report something other than a release version. */
export function isPlaceholderVersion(version: unknown): boolean {
  return typeof version !== 'string' || !release.test(version) || version === '0.0.0'
}

export function hasVersion(value: unknown): value is { version: string } {
  return isRecord(value) && typeof value.version === 'string' && value.version.length <= 64
}

export const versionMismatchMessage = 'Vessel was updated. Reload the page to use the new version.'

let mismatch: string | null = null
const listeners = new Set<() => void>()

/**
 * Records the API's version. Two release versions that differ mean this page was built for another server, so the
 * mismatch is kept until the page reloads: its code cannot change while it stays open. A placeholder on either side
 * is never a mismatch. Returns whether a mismatch is known.
 */
export function reportApiVersion(version: unknown): boolean {
  const web = webVersion()
  if (typeof version === 'string' && !isPlaceholderVersion(version) && !isPlaceholderVersion(web) && version !== web && version !== mismatch) {
    mismatch = version
    for (const listener of listeners) listener()
  }
  return mismatch !== null
}

/** The API version that differs from this page's, or null. */
export function apiVersionMismatch(): string | null {
  return mismatch
}

export function onVersionMismatch(listener: () => void): () => void {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

/** Test isolation only: the state is per page in the browser. */
export function resetVersionHandshake() {
  mismatch = null
}
