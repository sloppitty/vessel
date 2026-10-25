import { beforeEach, describe, expect, it, vi } from 'vitest'
import { accountIndex, credentialFailureMessage, credentialSaveMessage, credentialTransportAllowed, credentialTransportMessage } from './account-access'
import { ApiError } from './errors'
import { createWorkspaceApi } from './workspace'
import { credentialFixture, discoveryFixture, indexAccount } from '@/test/account-access-fixture'
import { credentialsFixture } from '@/test/identity-fixture'

const api = () => createWorkspaceApi(credentialsFixture)
beforeEach(() => vi.stubEnv('DEV', false))
function respond(body: unknown, status = 200) {
  const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status }))
  vi.stubGlobal('fetch', fetch)
  return fetch
}

describe('Account discovery contract', () => {
  it('trims pasted token whitespace before sending a name lookup', async () => {
    const fetch = respond(discoveryFixture)
    await api().discoverAccounts('lighter', discoveryFixture.address, undefined, ' \r\nro:fixture-only\t ')
    expect(fetch).toHaveBeenCalledWith('/api/venues/lighter/accounts/credential', expect.objectContaining({
      body: JSON.stringify({ address: discoveryFixture.address, token: 'ro:fixture-only' }),
    }))
  })
  it('loads names with the token in a POST body only and disables caching', async () => {
    const fetch = respond(discoveryFixture)
    const token = 'ro:fixture-only'
    await expect(api().discoverAccounts('lighter', discoveryFixture.address, undefined, token)).resolves.toEqual(discoveryFixture)
    expect(fetch).toHaveBeenCalledWith('/api/venues/lighter/accounts/credential', expect.objectContaining({
      method: 'POST', body: JSON.stringify({ address: discoveryFixture.address, token }),
      cache: 'no-store', credentials: 'same-origin', referrerPolicy: 'same-origin', redirect: 'error',
    }))
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)
  })
  it('blocks authenticated name lookup on plaintext LAN origins', async () => {
    vi.stubGlobal('location', { protocol: 'http:', hostname: '192.168.1.20' })
    const fetch = respond(discoveryFixture)
    await expect(api().discoverAccounts('lighter', discoveryFixture.address, undefined, 'ro:test')).rejects.toThrow(credentialTransportMessage)
    expect(fetch).not.toHaveBeenCalled()
  })
  it('allows authenticated name lookup on LAN HTTP in development', async () => {
    vi.stubEnv('DEV', true)
    vi.stubGlobal('location', { protocol: 'http:', hostname: '192.168.1.20' })
    const fetch = respond(discoveryFixture)
    await expect(api().discoverAccounts('lighter', discoveryFixture.address, undefined, 'ro:test')).resolves.toEqual(discoveryFixture)
    expect(fetch).toHaveBeenCalledOnce()
  })
  it('keeps int64 indices and decimal balances as exact strings', async () => {
    const fetch = respond(discoveryFixture)
    await expect(api().discoverAccounts('lighter', discoveryFixture.address)).resolves.toEqual(discoveryFixture)
    expect(fetch).toHaveBeenCalledWith(`/api/venues/lighter/accounts?address=${discoveryFixture.address}`, expect.objectContaining({ credentials: 'same-origin', referrerPolicy: 'same-origin', cache: 'no-store', redirect: 'error' }))
    respond(indexAccount, 201)
    const body = { venueId: 'lighter', sourceId: indexAccount.sourceId, name: 'Main', portfolioId: null }
    await expect(api().createAccount(body)).resolves.toEqual(indexAccount)
    expect(fetch).toHaveBeenCalledOnce()
  })
  it.each([
    { sourceId: 9007199254740992 }, { sourceId: '9223372036854775808' }, { sourceId: '01' },
    { sourceId: '-1' }, { accountValueUsd: 123 }, { collateralUsd: 123 }, { availableBalanceUsd: 'NaN' }, { accountType: 'unknown' },
    { existingAccountId: 'bad-id' }, { isEnabled: 'false' },
  ])('rejects malformed candidates %j', async patch => {
    respond({ ...discoveryFixture, accounts: [{ ...discoveryFixture.accounts[0], ...patch }] })
    await expect(api().discoverAccounts('lighter', discoveryFixture.address)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('rejects duplicate indices and a response for a different wallet or venue', async () => {
    for (const patch of [
      { accounts: [discoveryFixture.accounts[0], discoveryFixture.accounts[0]] },
      { address: `0x${'ab'.repeat(20)}` }, { venueId: 'other' },
    ]) {
      respond({ ...discoveryFixture, ...patch })
      await expect(api().discoverAccounts('lighter', discoveryFixture.address)).rejects.toMatchObject({ kind: 'invalid-response' })
    }
  })
  it('accepts previously imported disabled candidates', async () => {
    const body = { ...discoveryFixture, accounts: [{ ...discoveryFixture.accounts[0], existingAccountId: indexAccount.id, isEnabled: false }] }
    respond(body)
    await expect(api().discoverAccounts('lighter', discoveryFixture.address)).resolves.toEqual(body)
  })
  it.each([['../accounts', discoveryFixture.address], ['lighter', 'not-a-wallet']])('validates discovery input before fetching', async (venue, address) => {
    const fetch = respond({})
    await expect(api().discoverAccounts(venue, address)).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })
  it('rejects numeric account identities, but accepts older DTOs without sourceId', async () => {
    respond({ ...indexAccount, sourceId: 9007199254740992 })
    await expect(api().account(indexAccount.id)).rejects.toMatchObject({ kind: 'invalid-response' })
    const legacy: Partial<typeof indexAccount> = { ...indexAccount }
    delete legacy.sourceId
    respond(legacy)
    await expect(api().account(indexAccount.id)).resolves.toEqual(legacy)
  })
  it('validates the complete account index without numeric coercion', () => {
    expect(accountIndex('0')).toBe(true)
    expect(accountIndex('9223372036854775807')).toBe(true)
    expect(accountIndex('9223372036854775808')).toBe(false)
    expect(accountIndex('1e3')).toBe(false)
  })
})

describe('Credential API safety', () => {
  it.each([
    [401, /Sign in again/], [403, /trusted reverse proxy/], [404, /same version/],
    [409, /enable it/], [413, /512 characters/], [429, /Too many requests/],
    [502, /server credential vault/], [503, /server credential vault/], [504, /server credential vault/],
  ])('uses a local diagnostic for HTTP %s, not the error message', (status, message) => {
    const result = credentialFailureMessage(new ApiError('http', 'ro:never-display', status))
    expect(result).toMatch(message)
    expect(result).not.toContain('ro:never-display')
  })
  it('explains unknown save outcomes and never trusts arbitrary error messages', () => {
    expect(credentialFailureMessage(new ApiError('unavailable', 'ro:never-display'))).toMatch(/Refresh credential status/)
    for (const error of [new Error('ro:never-display'), new ApiError('http', 'ro:never-display', 400),
      new ApiError('invalid-response', 'ro:never-display'), { status: 403, message: 'ro:never-display' }]) {
      expect(credentialFailureMessage(error)).toBe(credentialSaveMessage)
    }
  })
  it('trims pasted token whitespace before sending a save', async () => {
    const fetch = respond(credentialFixture)
    await api().saveAccountCredential(indexAccount.id, ' \r\nro:fixture-only\t ')
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${indexAccount.id}/credential`, expect.objectContaining({
      body: JSON.stringify({ token: 'ro:fixture-only' }),
    }))
  })
  it('sends the secret in the PUT body only, and returns metadata only', async () => {
    const fetch = respond(credentialFixture)
    const token = 'ro:test-only-secret'
    await expect(api().saveAccountCredential(indexAccount.id, token)).resolves.toEqual(credentialFixture)
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${indexAccount.id}/credential`, expect.objectContaining({
      method: 'PUT', body: JSON.stringify({ token }), cache: 'no-store', redirect: 'error', credentials: 'same-origin', referrerPolicy: 'same-origin',
      headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'application/json', 'Content-Type': 'application/json' },
    }))
  })
  it.each([400, 401, 403, 404, 409, 500, 502, 503])('never echoes a credential error body at HTTP %s', async status => {
    respond({ detail: 'ro:do-not-show', token: 'ro:do-not-show' }, status)
    await expect(api().saveAccountCredential(indexAccount.id, 'ro:do-not-show')).rejects.toMatchObject({ message: credentialSaveMessage, status })
  })
  it('keeps exceptions from fetch secret-free', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('ro:do-not-show')))
    await expect(api().saveAccountCredential(indexAccount.id, 'ro:do-not-show')).rejects.toThrow(credentialSaveMessage)
  })
  it.each([
    { storageConfigured: 'yes', credential: null },
    { ...credentialFixture, token: 'ro:do-not-show' },
    { ...credentialFixture, credential: { ...credentialFixture.credential, token: 'ro:do-not-show' } },
    { ...credentialFixture, credential: { ...credentialFixture.credential, status: 'pending' } },
    { ...credentialFixture, credential: { ...credentialFixture.credential, expiresAt: 'bad' } },
    { ...credentialFixture, credential: { ...credentialFixture.credential, scope: 'ro:do-not-show' } },
  ])('rejects malformed metadata and secret fields', async body => {
    respond(body)
    await expect(api().accountCredential(indexAccount.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('allows metadata and removal when storage is not configured', async () => {
    respond({ storageConfigured: false, credential: null })
    await expect(api().accountCredential(indexAccount.id)).resolves.toEqual({ storageConfigured: false, credential: null })
    const fetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
    await expect(api().deleteAccountCredential(indexAccount.id)).resolves.toBeUndefined()
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${indexAccount.id}/credential`, expect.objectContaining({ method: 'DELETE' }))
  })
  it('rejects credential saving over LAN HTTP before fetch, but still allows metadata', async () => {
    vi.stubGlobal('location', { protocol: 'http:', hostname: '192.168.1.5' })
    const fetch = respond({ storageConfigured: true, credential: null })
    await expect(api().saveAccountCredential(indexAccount.id, 'ro:test')).rejects.toThrow(credentialTransportMessage)
    expect(fetch).not.toHaveBeenCalled()
    await api().accountCredential(indexAccount.id)
    expect(fetch).toHaveBeenCalledOnce()
  })
  it('allows credential saving over LAN HTTP in development without browser persistence', async () => {
    vi.stubEnv('DEV', true)
    vi.stubGlobal('location', { protocol: 'http:', hostname: '192.168.1.5' })
    const fetch = respond(credentialFixture)
    await expect(api().saveAccountCredential(indexAccount.id, 'ro:test')).resolves.toEqual(credentialFixture)
    expect(fetch).toHaveBeenCalledOnce()
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)
  })
  it.each(['development', 'staging', 'test'])('a named mode %s does not bypass transport without DEV', mode => {
    vi.stubEnv('MODE', mode)
    expect(credentialTransportAllowed({ protocol: 'http:', hostname: 'vessel.local' })).toBe(false)
  })
  it.each([
    ['https:', 'vessel.example', true], ['http:', 'localhost', true], ['http:', '127.0.0.1', true],
    ['http:', '[::1]', true], ['http:', '192.168.1.5', false], ['http:', 'localhost.example', false],
    ['http:', 'vessel.local', false], ['file:', 'localhost', false],
  ])('checks transport %s %s', (protocol, hostname, allowed) => {
    expect(credentialTransportAllowed({ protocol, hostname })).toBe(allowed)
  })
  it.each([
    ['http:', 'vessel.local', true], ['https:', 'vessel.local', true],
    ['file:', 'localhost', false], ['ftp:', 'vessel.local', false],
  ])('development still restricts protocols %s %s', (protocol, hostname, allowed) => {
    vi.stubEnv('DEV', true)
    expect(credentialTransportAllowed({ protocol, hostname })).toBe(allowed)
  })
})
