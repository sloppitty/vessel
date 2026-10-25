import { describe, expect, it, vi } from 'vitest'
import { isManualExecutionCorrection, isManualExecutionInput, isManualExecutions, newDiaryRequestId, type ManualExecutionInput } from './diary'
import { createWorkspaceApi } from './workspace'
import { isSavedPlay, isPlayPlan, emptyStructuredReview, type SavedPlay } from './plays'
import { createDraft } from '@/features/plays/draft'
import { planFromDraft } from '@/features/plays/saved'
import { credentialsFixture } from '@/test/identity-fixture'

const id = '00000000-0000-4000-8000-000000000001'
const at = '2026-10-13T10:00:00Z'
const input: ManualExecutionInput = {
  requestId: id, expectedVersion: 2, role: 'entry', entryId: 'entry-1', occurredAtUtc: at,
  side: 'buy', quantity: '1.000000000000000001', price: '100.00', fee: null, feeCurrency: null, source: 'Broker statement',
}
function saved(): SavedPlay {
  return { summary: { id, title: 'Manual', status: 'planned', accountId: id, venueId: 'manual', instrument: 'BTC',
    instrumentSource: 'manual', direction: 'long', planRevision: 1, version: 2, cancelReason: null,
    createdAtUtc: at, updatedAtUtc: at, plannedAtUtc: at, endedAtUtc: null, hasReview: false },
    plan: planFromDraft(createDraft()), drawings: {}, review: '' }
}
const record = { ...input, id, createdAtUtc: at, updatedAtUtc: at }
const ledger = () => ({ play: saved(), records: [record],
  history: [{ id, executionId: id, before: null, after: record, reason: null, occurredAtUtc: at }],
  totals: { enteredQuantity: input.quantity, exitedQuantity: '0', openQuantity: input.quantity, grossResultUsd: '0', netResultUsd: null, feesComplete: false }, notice: 'Manual scope.' })

describe('diary boundary', () => {
  it('keeps exact strings, accepts unknown fees and nullable creation reasons, and separates known zero', () => {
    expect(isManualExecutions(ledger())).toBe(true)
    expect(isManualExecutionInput(input)).toBe(true)
    expect(isManualExecutionInput({ ...input, fee: '0', feeCurrency: 'USDC' })).toBe(true)
    expect(isManualExecutionInput({ ...input, quantity: '1.230e-5', price: '+1.00E+2' })).toBe(true)
    expect(isManualExecutionInput({ ...input, quantity: '0e+5' })).toBe(false)
    expect(isManualExecutions({ ...ledger(), totals: { ...ledger().totals, grossResultUsd: '-1.230e-5' } })).toBe(true)
    expect(isManualExecutionInput({ ...input, fee: '0' })).toBe(false)
    expect(isManualExecutionInput({ ...input, feeCurrency: 'USDC' })).toBe(false)
    expect(isManualExecutions({ ...ledger(), totals: { ...ledger().totals, netResultUsd: 0 } })).toBe(false)
  })
  it.each([
    { role: 'entry', entryId: null }, { role: 'exit', entryId: 'entry-1' }, { occurredAtUtc: '2026-02-31T10:00:00Z' },
    { occurredAtUtc: '2026-10-13T10:00:00-05:00' }, { source: ' ' }, { source: 'line\nbreak' },
    { quantity: '0' }, { price: '1e200' }, { quantity: 1 }, { fee: '-1', feeCurrency: 'USDC' }, { expectedVersion: 0 },
  ])('rejects invalid facts %j', patch => expect(isManualExecutionInput({ ...input, ...patch })).toBe(false))
  it('requires a reason on correction and uses GUID identities without secure-context randomUUID', () => {
    expect(isManualExecutionCorrection({ ...input, reason: ' ' })).toBe(false)
    expect(isManualExecutionCorrection({ ...input, reason: 'Corrected statement' })).toBe(true)
    expect(newDiaryRequestId()).toMatch(/^[\da-f]{8}-[\da-f]{4}-4[\da-f]{3}-[89ab][\da-f]{3}-[\da-f]{12}$/)
  })
  it('defaults old models but validates origin-aware compatibility plans and structured answers', () => {
    const play = saved()
    expect(isSavedPlay(play)).toBe(true)
    expect(isSavedPlay({ ...play, structuredReview: null })).toBe(true)
    expect(isSavedPlay({ ...play, structuredReview: emptyStructuredReview() })).toBe(true)
    const plan = { ...play.plan, entries: [], leverage: '0' }
    expect(isPlayPlan(plan)).toBe(false)
    expect(isSavedPlay({ ...play, plan })).toBe(false)
    expect(isSavedPlay({ ...play, plan, summary: { ...play.summary, origin: 'retrospective' } })).toBe(true)
    expect(isSavedPlay({ ...play, summary: { ...play.summary, origin: 'invented' } })).toBe(false)
    expect(isSavedPlay({ ...play, structuredReview: { ...emptyStructuredReview(), adherence: 'excellent' } })).toBe(false)
  })
  it('writes authenticated no-store requests and preserves payload retry identity', async () => {
    const fetcher = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response(JSON.stringify(ledger()), { status: 200 }))
    try {
      const api = createWorkspaceApi(credentialsFixture)
      await api.addManualExecution!(id, input)
      await api.addManualExecution!(id, input)
      const calls = fetcher.mock.calls
      expect(calls[0]![0]).toBe(`/api/plays/${id}/manual-executions`)
      expect(calls[0]![1]).toMatchObject({ method: 'POST', cache: 'no-store', credentials: 'same-origin', referrerPolicy: 'same-origin',
        headers: { 'X-Vessel-User': credentialsFixture.userId }, body: JSON.stringify(input) })
      expect(calls[1]![1]!.body).toEqual(calls[0]![1]!.body)
      await api.correctManualExecution!(id, id, { ...input, reason: 'Statement fix' })
      expect(fetcher.mock.lastCall![0]).toBe(`/api/plays/${id}/manual-executions/${id}`)
      expect(fetcher.mock.lastCall![1]!.method).toBe('PUT')
    } finally { fetcher.mockRestore() }
  })
  it('rejects invalid inputs before fetch and propagates stale writes without retrying them', async () => {
    const fetcher = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ error: 'Reload the Play.' }), { status: 409 }))
    try {
      const api = createWorkspaceApi(credentialsFixture)
      await expect(api.addManualExecution!(id, { ...input, quantity: '1e200' })).rejects.toMatchObject({ kind: 'invalid-response' })
      expect(fetcher).not.toHaveBeenCalled()
      await expect(api.addManualExecution!(id, input)).rejects.toMatchObject({ status: 409 })
      expect(fetcher).toHaveBeenCalledOnce()
    } finally { fetcher.mockRestore() }
  })
})
