import { describe, expect, it, vi } from 'vitest'
import { credentialFailureMessage } from './account-access'
import { ApiError, errorMessage, rateLimitMessage } from './errors'
import { createWorkspaceApi } from './workspace'
import { credentialsFixture } from '@/test/identity-fixture'
import { accountFixture } from '@/test/workspace-fixture'

describe('per-user wait notices', () => {
  it('keeps Retry-After, including on protected credential calls', async () => {
    const api = createWorkspaceApi(credentialsFixture)
    const fetch = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ code: 'rate-limited', detail: 'ro:private' }),
      { status: 429, headers: { 'Retry-After': '60' } }))
    vi.stubGlobal('fetch', fetch)
    const { id } = accountFixture
    const address = `0x${'ab'.repeat(20)}`
    const query = { instrument: 'BTC', interval: '1h' as const }
    for (const run of [
      () => api.candles(id, { ...query, refresh: true }),
      () => api.marketStream(id, query, new AbortController().signal, vi.fn()),
      () => api.discoverAccounts('lighter', address),
      () => api.discoverAccounts('lighter', address, undefined, 'ro:test-only'),
      () => api.saveAccountCredential(id, 'ro:test-only'),
    ]) {
      const cause = await run().catch((error: unknown) => error)
      expect(cause).toMatchObject({ status: 429, retryAtUtc: expect.any(String) })
      expect(errorMessage(cause, 'Failed.')).toBe('Too many requests. Wait 60 seconds before trying again.')
      expect(credentialFailureMessage(cause)).toBe('Too many requests. Wait 60 seconds before trying again.')
    }
    expect(fetch).toHaveBeenCalledTimes(5) // No automatic retry.
  })

  it('uses an HTTP-date deadline and safe copy without reading protected failure bodies', async () => {
    const now = Date.parse('2026-10-17T12:00:00Z')
    const clock = vi.spyOn(Date, 'now').mockReturnValue(now)
    try {
      const response = new Response('private credential detail', { status: 429, headers: { 'Retry-After': new Date(now + 30_000).toUTCString() } })
      const json = vi.spyOn(response, 'json')
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response))
      const cause = await createWorkspaceApi(credentialsFixture).saveAccountCredential(accountFixture.id, 'ro:test-only').catch((error: unknown) => error)
      expect(errorMessage(cause, '')).toBe('Too many requests. Wait 30 seconds before trying again.')
      expect(json).not.toHaveBeenCalled()
    } finally { clock.mockRestore() }
  })

  it.each([undefined, null, 'invalid', '2000-01-01T00:00:00Z'])('falls back safely for a missing, invalid or elapsed deadline: %s', at => {
    expect(rateLimitMessage(at)).toBe('Too many requests. Wait a moment before trying again.')
  })

  it('rounds up a positive wait and uses singular seconds', () => {
    expect(rateLimitMessage(new Date(Date.now() + 500).toISOString())).toBe('Too many requests. Wait 1 second before trying again.')
    expect(errorMessage(new ApiError('http', 'Another failure.', 409), '')).toBe('Another failure.')
  })
})
