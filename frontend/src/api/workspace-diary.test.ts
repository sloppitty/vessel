import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from './workspace'
import { accountFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { operationWithFillsFixture as operationFixture } from '@/test/activity-fixture'
import { reviewExclusionsPath } from './review'
import { retrospectivePreviewPath } from './retrospective'
import { credentialsFixture } from '@/test/identity-fixture'

afterEach(() => vi.unstubAllGlobals())
const preview = { eligible: false, reasons: ['Operation remains open.'], fingerprint: 'fixture-fingerprint', operation: operationFixture, notice: 'Full retained facts.' }
const requestId = '00000000-0000-4000-8000-000000000003'

describe('diary WorkspaceApi integration', () => {
  it('fetches an authenticated, no-store retrospective preview using the sibling module path and fill validator', async () => {
    const fetcher = vi.fn(async () => new Response(JSON.stringify(preview), { status: 200 }))
    vi.stubGlobal('fetch', fetcher)
    const signal = new AbortController().signal
    const result = await createWorkspaceApi(credentialsFixture).retrospectivePreview!(accountFixture.id, operationFixture.id, signal)
    expect(result.operation.fills[0]!.fill.price).toBe('100.123456789123456789')
    expect(fetcher).toHaveBeenCalledWith(retrospectivePreviewPath(accountFixture.id, operationFixture.id), expect.objectContaining({
      cache: 'no-store', signal: expect.any(AbortSignal), headers: expect.objectContaining({ 'X-Vessel-User': credentialsFixture.userId }),
    }))
  })
  it('rejects another operation/account in a preview, rather than submitting facts from a mismatched record', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(preview), { status: 200 })))
    await expect(createWorkspaceApi(credentialsFixture).retrospectivePreview!(requestId, operationFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('creates a retrospective record with the exact fingerprint and GUID and accepts its empty compatibility plan', async () => {
    const retrospective = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, origin: 'retrospective', status: 'closed' },
      plan: { ...savedPlayFixture.plan, leverage: '0', entries: [] } }
    const fetcher = vi.fn(async () => new Response(JSON.stringify(retrospective), { status: 200 }))
    vi.stubGlobal('fetch', fetcher)
    const input = { requestId, accountId: accountFixture.id, operationId: operationFixture.id, expectedFingerprint: preview.fingerprint, title: 'After the trade' }
    await expect(createWorkspaceApi(credentialsFixture).createRetrospectivePlay!(input)).resolves.toEqual(retrospective)
    expect(fetcher).toHaveBeenCalledWith('/api/plays/retrospective', expect.objectContaining({
      method: 'POST', body: JSON.stringify(input), cache: 'no-store', signal: expect.any(AbortSignal),
    }))
  })
  it('passes all Review filters including exclusion paging to the sibling validator/path', async () => {
    const document = { items: [{ playId: savedPlayFixture.summary.id, title: 'Needs fees', accountId: accountFixture.id,
      accountName: accountFixture.name, instrument: 'BTC', reasons: ['Fill 7 has an unknown fee.'] }],
      total: 51, offset: 50, nextOffset: null, notice: 'Retained facts only.' }
    const fetcher = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(document), { status: 200 }))
    vi.stubGlobal('fetch', fetcher)
    const query = { period: 'month' as const, portfolio: 'unassigned', accountId: accountFixture.id, instrument: 'BTC', offset: 50 }
    await expect(createWorkspaceApi(credentialsFixture).reviewExclusions!(query)).resolves.toEqual(document)
    expect(fetcher.mock.calls[0]![0]).toBe(reviewExclusionsPath(query))
  })
})
