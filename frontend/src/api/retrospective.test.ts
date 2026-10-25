import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from './workspace'
import { isCreateRetrospectivePlayRequest, isRetrospectivePreview, retrospectivePlayPath, retrospectivePreviewPath } from './retrospective'
import { retrospectiveFixture, retrospectiveSavedFixture } from '@/test/retrospective.fixtures'
import { unknownPeakOperationFixture } from '@/test/activity-fixture'
import { credentialsFixture } from '@/test/identity-fixture'

const op = retrospectiveFixture.operation
const request = { requestId: 'bbbbbbbb-1234-1234-1234-123456789abc', accountId: op.accountId, operationId: op.id,
  expectedFingerprint: retrospectiveFixture.fingerprint, title: 'Owner confirmed round trip' }
describe('retrospective API contract', () => {
  it('accepts exact original facts and blocked mixed records without fabricating a plan', () => {
    expect(isRetrospectivePreview(retrospectiveFixture)).toBe(true)
    expect(isRetrospectivePreview({ ...retrospectiveFixture, eligible: false, reasons: ['Order 2 spans another operation.'],
      operation: { ...op, playAssociation: 'mixed' } })).toBe(true)
    expect(isCreateRetrospectivePlayRequest(request)).toBe(true)
    expect(retrospectivePlayPath()).toBe('/api/plays/retrospective')
  })
  it('accepts an ineligible preview of an operation whose peak quantity is unknown', () => {
    const peak = { ...retrospectiveFixture, eligible: false, reasons: [...unknownPeakOperationFixture.coverage], operation: unknownPeakOperationFixture }
    expect(isRetrospectivePreview(peak)).toBe(true)
    expect(isRetrospectivePreview({ ...peak, operation: { ...unknownPeakOperationFixture, coverage: [] } })).toBe(false)
  })
  it.each([
    { fingerprint: '' }, { reasons: [''] }, { eligible: 'yes' },
    { operation: { ...op, playAssociation: 'mixed' } },
    { operation: { ...op, status: 'open', closedAtUtc: null } },
    { operation: { ...op, fills: Array.from({ length: 201 }, () => op.fills[0]), fillCount: 201 } },
    { operation: { ...op, fills: [{ ...op.fills[0], fill: { ...op.fills[0]!.fill, accountId: 'other' } }] } },
  ])('rejects malformed or contradictory previews %#', change => {
    expect(isRetrospectivePreview({ ...retrospectiveFixture, ...change })).toBe(false)
  })
  it.each([{ requestId: '' }, { accountId: '../../' }, { operationId: 'fake' }, { title: ' ' },
    { expectedFingerprint: '' }, { title: 'x'.repeat(201) }, { review: 'x'.repeat(10_001) }])('rejects malformed confirmation %#', change => {
    expect(isCreateRetrospectivePlayRequest({ ...request, ...change })).toBe(false)
  })
  it('encodes operation identities in query strings', () => {
    const params = new URL(retrospectivePreviewPath(op.accountId, op.id), 'http://localhost').searchParams
    expect(params.get('operationId')).toBe(op.id)
    expect(params.get('accountId')).toBe(op.accountId)
  })
  it.each([200, 201, 202])('uses authenticated no-store reads and sends only server identity/fingerprint on confirmation with HTTP %i', async status => {
    const fetch = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(retrospectiveFixture)))
      .mockResolvedValueOnce(new Response(JSON.stringify(retrospectiveSavedFixture), { status }))
    vi.stubGlobal('fetch', fetch)
    const api = createWorkspaceApi(credentialsFixture)
    const controller = new AbortController()
    expect(await api.retrospectivePreview(op.accountId, op.id, controller.signal)).toEqual(retrospectiveFixture)
    expect(fetch).toHaveBeenNthCalledWith(1, retrospectivePreviewPath(op.accountId, op.id), expect.objectContaining({
      cache: 'no-store', credentials: 'same-origin', referrerPolicy: 'same-origin', headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'application/json' },
      signal: expect.any(AbortSignal),
    }))
    expect(await api.createRetrospectivePlay(request, controller.signal)).toEqual(retrospectiveSavedFixture)
    expect(JSON.parse(fetch.mock.calls[1]![1].body)).toEqual(request)
    expect(fetch.mock.calls[1]![1].method).toBe('POST')
    controller.abort()
    expect(fetch.mock.calls[0]![1].signal.aborted).toBe(true)
  })
  it('rejects a valid preview for a different requested account or operation', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(new Response(JSON.stringify(retrospectiveFixture)))))
    const api = createWorkspaceApi(credentialsFixture)
    await expect(api.retrospectivePreview('bbbbbbbb-1234-1234-1234-123456789abc', op.id)).rejects.toThrow()
    await expect(api.retrospectivePreview(op.accountId, 'bbbbbbbb-1234-1234-1234-123456789abc:long')).rejects.toThrow()
  })
})
