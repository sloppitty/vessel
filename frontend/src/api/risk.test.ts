import { credentialsFixture } from '@/test/identity-fixture'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { emptyOpenRisk } from '@/test/workspace-fixture'
import { isOpenRisk, isOpenRiskPreview, isRiskSettings, type OpenRisk } from './risk'
import { createWorkspaceApi } from './workspace'

const play = { playId: '11111111-1111-1111-1111-111111111111', title: 'BTC long', direction: 'long', status: 'planned',
  accountId: '22222222-2222-2222-2222-222222222222', riskUsd: '12.5', reason: null, rounded: false, protection: null } as const
const document: OpenRisk = { ...emptyOpenRisk('all'), long: { knownUsd: '12.5', knownCount: 1, unknownCount: 0, rounded: false }, plays: [play] }

afterEach(() => vi.unstubAllGlobals())

describe('open risk documents', () => {
  it('accepts a document whose counts match its Plays', () => {
    expect(isOpenRisk(document, 'all')).toBe(true)
    expect(isOpenRisk(emptyOpenRisk('account:22222222-2222-2222-2222-222222222222'), 'account:22222222-2222-2222-2222-222222222222')).toBe(true)
  })

  it('refuses a document for another scope, a negative risk or counts that do not add up', () => {
    expect(isOpenRisk(document, 'unassigned')).toBe(false)
    // Risk is never negative: a locked-in stop adds zero, never a negative amount that offsets another.
    expect(isOpenRisk({ ...document, plays: [{ ...play, riskUsd: '-5' }] }, 'all')).toBe(false)
    expect(isOpenRisk({ ...document, long: { ...document.long, knownUsd: '-5' } }, 'all')).toBe(false)
    expect(isOpenRisk({ ...document, long: { ...document.long, knownCount: 2 } }, 'all')).toBe(false)
    // Unknown is null with a reason, never both and never neither.
    expect(isOpenRisk({ ...document, plays: [{ ...play, riskUsd: null }] }, 'all')).toBe(false)
    // Protection counts must match the Plays' recorded coverage.
    expect(isOpenRisk({ ...document, protection: { notCovered: 1, unverified: 0 } }, 'all')).toBe(false)
    expect(isOpenRisk({ ...document, plays: [{ ...play, protection: 'blocked' }] }, 'all')).toBe(false)
  })

  it('checks previews against their request and settings as decimal or null', () => {
    const request = { playId: null, accountId: play.accountId, plan: { direction: 'short' as const, sizingMode: 'quantity' as const, size: '1', leverage: '1', entries: [] }, instrumentSource: 'venue' as const }
    expect(isOpenRiskPreview({ playId: null, direction: 'short', riskUsd: '2', reason: null, rounded: false }, request)).toBe(true)
    expect(isOpenRiskPreview({ playId: null, direction: 'long', riskUsd: '2', reason: null, rounded: false }, request)).toBe(false)
    expect(isRiskSettings({ openRiskLimitPercent: null })).toBe(true)
    expect(isRiskSettings({ openRiskLimitPercent: '3.5' })).toBe(true)
    expect(isRiskSettings({ openRiskLimitPercent: 3.5 })).toBe(false)
  })

  it('never reads open risk in Paper mode', async () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    const paper = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    await expect(paper.openRisk('all')).rejects.toMatchObject({ kind: 'unavailable' })
    await expect(paper.riskSettings()).rejects.toMatchObject({ kind: 'unavailable' })
    expect(fetch).not.toHaveBeenCalled()
  })
})
