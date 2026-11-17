import { describe, expect, it, vi } from 'vitest'
import { createApiCall } from './request'
import { apiVersionMismatch, isPlaceholderVersion, reportApiVersion, versionMismatchMessage, webVersion } from './version'
import { credentialsFixture } from '@/test/identity-fixture'

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

describe('version handshake', () => {
  it('reports a placeholder for this page outside a build', () => {
    expect(webVersion()).toBe('development')
    vi.stubGlobal('__VESSEL_VERSION__', '0.5.0')
    expect(webVersion()).toBe('0.5.0')
  })

  it.each(['development', 'unknown', '0.0.0', '', '1.2', '1.2.3-dev', 42])('treats %j as a placeholder', version => {
    expect(isPlaceholderVersion(version)).toBe(true)
  })

  it('never reports a mismatch when either side is a placeholder', () => {
    expect(reportApiVersion('0.5.0')).toBe(false)
    vi.stubGlobal('__VESSEL_VERSION__', '0.4.2')
    expect(reportApiVersion('unknown')).toBe(false)
    expect(reportApiVersion('0.0.0')).toBe(false)
    expect(reportApiVersion('0.4.2')).toBe(false)
    expect(apiVersionMismatch()).toBeNull()
  })

  it('keeps a differing release version until the page reloads', () => {
    vi.stubGlobal('__VESSEL_VERSION__', '0.4.2')
    expect(reportApiVersion('0.5.0')).toBe(true)
    // A stale read of the old version (the sign-in system read after a mode switch) never hides it again.
    expect(reportApiVersion('0.4.2')).toBe(true)
    expect(apiVersionMismatch()).toBe('0.5.0')
  })
})

describe('a body this page cannot read', () => {
  it('says to reload, not that the data is incompatible, when the API runs another version', async () => {
    vi.stubGlobal('__VESSEL_VERSION__', '0.4.2')
    const fetch = vi.fn(async (path: string) => json(path === '/api/system' ? { version: '0.5.0', unexpected: true } : { renamed: [] }))
    vi.stubGlobal('fetch', fetch)
    const call = createApiCall(credentialsFixture)
    const failure = call('/api/portfolios', { timeout: 1_000, validate: (v): v is unknown[] => Array.isArray(v), invalidData: 'The API returned incompatible workspace data.' })
    await expect(failure).rejects.toMatchObject({ kind: 'invalid-response', code: 'version-mismatch', message: versionMismatchMessage })
    expect(apiVersionMismatch()).toBe('0.5.0')
    // Once known, later failures say the same without asking again.
    await expect(call('/api/accounts', { timeout: 1_000, validate: (v): v is unknown[] => Array.isArray(v) })).rejects.toThrow(versionMismatchMessage)
    expect(fetch.mock.calls.filter(([path]) => path === '/api/system')).toHaveLength(1)
  })

  it.each([['the same version', '0.4.2'], ['a placeholder', 'unknown']])('keeps the incompatible-data message when the API reports %s', async (_, version) => {
    vi.stubGlobal('__VESSEL_VERSION__', '0.4.2')
    vi.stubGlobal('fetch', vi.fn(async (path: string) => json(path === '/api/system' ? { version } : { renamed: [] })))
    await expect(createApiCall(credentialsFixture)('/api/portfolios', { timeout: 1_000, validate: (v): v is unknown[] => Array.isArray(v),
      invalidData: 'The API returned incompatible workspace data.' })).rejects.toThrow('The API returned incompatible workspace data.')
    expect(apiVersionMismatch()).toBeNull()
  })

  it('keeps the message when the version cannot be read, and never checks from a public call', async () => {
    vi.stubGlobal('__VESSEL_VERSION__', '0.4.2')
    const fetch = vi.fn(async (path: string) => path === '/api/system' ? new Response(null, { status: 503 }) : json({ renamed: [] }))
    vi.stubGlobal('fetch', fetch)
    const validate = (v: unknown): v is unknown[] => Array.isArray(v)
    await expect(createApiCall(credentialsFixture)('/api/portfolios', { timeout: 1_000, validate })).rejects.toThrow('incompatible workspace data')
    fetch.mockClear()
    await expect(createApiCall({ kind: 'public' })('/api/auth/config', { timeout: 1_000, validate })).rejects.toThrow('incompatible workspace data')
    expect(fetch).toHaveBeenCalledOnce()
  })

  it('never checks from a development page, which can never report a mismatch', async () => {
    const fetch = vi.fn(async () => json({ renamed: [] }))
    vi.stubGlobal('fetch', fetch)
    await expect(createApiCall(credentialsFixture)('/api/portfolios', { timeout: 1_000, validate: (v): v is unknown[] => Array.isArray(v) }))
      .rejects.toThrow('incompatible workspace data')
    expect(fetch).toHaveBeenCalledOnce()
  })
})
