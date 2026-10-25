import { describe, expect, it, vi } from 'vitest'
import { ApiError } from './errors'
import { getSystem } from './system'
import { systemFixture } from '@/test/system-fixture'
import { indexVenue } from '@/test/account-access-fixture'
import { credentialsFixture } from '@/test/identity-fixture'

function mockResponse(body: unknown, status = 200) {
  const request = vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status }))
  vi.stubGlobal('fetch', request)
  return request
}

describe('GET /api/system', () => {
  it('accepts account-index sources and optional discovery/credential capabilities', async () => {
    const body = { ...systemFixture, venues: [indexVenue, ...systemFixture.venues.filter(v => v.id !== indexVenue.id)] }
    mockResponse(body)
    await expect(getSystem(credentialsFixture)).resolves.toEqual(body)
  })
  it.each(['accountDiscovery', 'readOnlyCredential', 'manualExecutions', 'currentOrderBook'])('rejects a non-boolean %s capability', async name => {
    mockResponse({ ...systemFixture, venues: [
      { ...indexVenue, capabilities: { ...indexVenue.capabilities, [name]: 'yes' } },
      ...systemFixture.venues.filter(v => v.id !== indexVenue.id),
    ] })
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it.each([true, false])('accepts explicit manual execution capability %s without inferring it from venue names', async manualExecutions => {
    const body = { ...systemFixture, venues: systemFixture.venues.map(venue => ({
      ...venue, capabilities: { ...venue.capabilities, manualExecutions },
    })) }
    mockResponse(body)
    await expect(getSystem(credentialsFixture)).resolves.toEqual(body)
  })
  it('accepts the native trade placeholder and HTTPS read-only credential setup link', async () => {
    const body = { ...systemFixture, venues: [
      { ...indexVenue, tradeUrlTemplate: 'https://trade.example/{venueContractId}' },
      ...systemFixture.venues.filter(v => v.id !== indexVenue.id),
    ] }
    mockResponse(body)
    await expect(getSystem(credentialsFixture)).resolves.toEqual(body)
  })
  it.each(['https://trade.example/{unknown}', 'https://trade.example/{instrument}/{unknown}', 'https://{venueContractId', 'https://user:pass@trade.example/{instrument}'])('rejects unsafe or unresolved trade template %s', async tradeUrlTemplate => {
    mockResponse({ ...systemFixture, venues: [{ ...indexVenue, tradeUrlTemplate }, ...systemFixture.venues.filter(v => v.id !== indexVenue.id)] })
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it.each(['http://venue.example/tokens', 'javascript:alert(1)', 'https://user:pass@venue.example/tokens', 'https://'])('rejects unsafe credential setup URL %s', async credentialSetupUrl => {
    mockResponse({ ...systemFixture, venues: [{ ...indexVenue, credentialSetupUrl }, ...systemFixture.venues.filter(v => v.id !== indexVenue.id)] })
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('keeps logo keys from the fixed set, including the Robinhood Lighter venue', async () => {
    mockResponse(systemFixture)
    const system = await getSystem(credentialsFixture)
    expect(Object.fromEntries(system.venues.map(v => [v.id, v.logo]))).toEqual({
      hyperliquid: 'hyperliquid', 'lighter-rh': 'lighter-rh', quantfury: undefined, manual: 'manual',
    })
  })
  it.each(['binance', 'https://venue.example/logo.png', '../lighter', '', 7, null, {}, 'Lighter', 'constructor', '__proto__'])('treats unknown logo %j as absent without rejecting the contract', async logo => {
    mockResponse({ ...systemFixture, venues: systemFixture.venues.map(v => v.id === 'hyperliquid' ? { ...v, logo } : v) })
    const system = await getSystem(credentialsFixture)
    const hyperliquid = system.venues.find(v => v.id === 'hyperliquid')!
    expect(hyperliquid).not.toHaveProperty('logo')
    expect(hyperliquid.name).toBe('Hyperliquid')
  })
  it('uses the exact path and the session transport, with nothing secret in the URL', async () => {
    const request = mockResponse(systemFixture)
    await expect(getSystem(credentialsFixture)).resolves.toEqual(systemFixture)
    expect(request).toHaveBeenCalledWith('/api/system', expect.objectContaining({
      method: 'GET', headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'application/json' },
      credentials: 'same-origin', referrerPolicy: 'same-origin', cache: 'no-store', redirect: 'error', signal: expect.any(AbortSignal),
    }))
  })

  it('rejects a session without a user ID without making a request', async () => {
    const request = mockResponse(systemFixture)
    await expect(getSystem({ ...credentialsFixture, userId: 'not-a-user' })).rejects.toMatchObject({ kind: 'unauthorized' })
    expect(request).not.toHaveBeenCalled()
  })

  it.each([
    [401, 'unauthorized', 'Your session ended. Sign in again.'],
    [403, 'http', 'Vessel refused this request.'],
  ] as const)('reports HTTP %s without exposing response content', async (status, kind, message) => {
    mockResponse({ detail: 'Sensitive server details' }, status)
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind, status, message })
  })

  it.each([500, 502, 503])('reports HTTP %s without exposing response content', async (status) => {
    mockResponse({ detail: 'Sensitive server details' }, status)
    const cause = await getSystem(credentialsFixture).catch((error: unknown) => error)
    expect(cause).toMatchObject({ kind: 'http', status })
    expect((cause as ApiError).message).not.toContain('Sensitive')
  })

  it.each(['network', 'timeout'])('reports %s failures without leaking internal errors', async (kind) => {
    const error = kind === 'timeout' ? new DOMException('Timed out', 'TimeoutError') : new Error('private detail in transport error')
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(error))
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind: 'unavailable' })
    await expect(getSystem(credentialsFixture)).rejects.not.toThrow('private detail')
  })

  it('reports other HTTP errors separately', async () => {
    mockResponse({}, 404)
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind: 'http', status: 404 })
  })

  it('rejects non-JSON success responses', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>proxy page</html>')))
    await expect(getSystem(credentialsFixture)).rejects.toBeInstanceOf(ApiError)
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it.each([
    null,
    {},
    { ...systemFixture, owner: { id: 'not-an-owner-id', displayName: 'Owner' } },
    { ...systemFixture, allowsConcurrentPlays: false },
    { ...systemFixture, marketScope: 'spot' },
    { ...systemFixture, venues: [{ id: 'hyperliquid', name: 'Hyperliquid', status: 'connected' }] },
    { ...systemFixture, venues: [{ id: 'hyperliquid', name: 'Hyperliquid', status: 'connected' }] },
    // Every venue states what it can do, and manual accounts must remain possible.
    { ...systemFixture, venues: systemFixture.venues.map(venue => venue.id === 'hyperliquid' ? { ...venue, capabilities: { sync: true } } : venue) },
    { ...systemFixture, venues: systemFixture.venues.filter(venue => venue.id !== 'manual') },
    { ...systemFixture, venues: [...systemFixture.venues, systemFixture.venues[0]] },
    { ...systemFixture, venues: systemFixture.venues.map(venue => venue.id === 'hyperliquid' ? { ...venue, tradeUrlTemplate: 'http://example.test/{instrument}' } : venue) },
    { ...systemFixture, venues: systemFixture.venues.map(venue => venue.id === 'hyperliquid' ? { ...venue, tradeUrlTemplate: 'https://example.test/trade' } : venue) },
    { ...systemFixture, venues: systemFixture.venues.map(venue => venue.id === 'hyperliquid' ? { ...venue, intervals: ['2M'] } : venue) },
    { ...systemFixture, venues: systemFixture.venues.map(venue => venue.id === 'hyperliquid' ? { ...venue, priceRule: 'rounded' } : venue) },
  ])('rejects incompatible system data %#', async (body) => {
    mockResponse(body)
    await expect(getSystem(credentialsFixture)).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it('accepts a configured owner rather than requiring the example UUID', async () => {
    const custom = { ...systemFixture, owner: { id: '22222222-2222-2222-2222-222222222222', displayName: 'Configured owner' } }
    mockResponse(custom)
    await expect(getSystem(credentialsFixture)).resolves.toEqual(custom)
  })
  it('accepts any number of venues with their own capabilities', async () => {
    const other = { id: 'other-venue', name: 'Other', status: 'read-only', source: 'evm-address', quoteAsset: 'USDC', tradeUrlTemplate: null,
      intervals: ['1m', '1h', '1d'], priceRule: 'tick-size',
      capabilities: { sync: true, instruments: true, orders: false, candles: false, marketContext: false, stream: false, stablecoinWallet: false } }
    const body = { ...systemFixture, venues: [...systemFixture.venues, other] }
    mockResponse(body)
    await expect(getSystem(credentialsFixture)).resolves.toEqual(body)
  })
  it('accepts the current core stage with a read-only Hyperliquid capability', async () => {
    const core = { ...systemFixture, stage: 'core', venues: systemFixture.venues.map(venue => venue.id === 'hyperliquid' ? { ...venue, status: 'read-only' } : venue) }
    mockResponse(core)
    await expect(getSystem(credentialsFixture)).resolves.toEqual(core)
  })
})
