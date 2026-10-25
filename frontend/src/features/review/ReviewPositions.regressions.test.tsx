import { act, render, renderHook, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import { isReviewDocument, reviewPath, type ReviewDocument, type ReviewQuery } from '@/api/review'
import { emptyReview, reviewFixture } from '@/test/review-fixture'
import { preferenceKey, scopePreferences, userPreferenceKey } from '@/lib/preference-scope'
import { ReviewPage } from './ReviewPage'
import { ReviewSettings } from './ReviewSettings'
import { readReviewPreferences, useReviewPreferences } from './reviewPreferences'
import { credentialsFixture } from '@/test/identity-fixture'

// Same populations as the server: one scored Play and two unassigned positions.
const mixedMetrics = {
  ...reviewFixture.metrics, positions: 2, riskCount: 1, reviewed: 1,
  batting: '0.6666666666666666666666666667', expectancy: '87.5617283945', averageR: '2',
  signDiffers: 1, localUnknown: 0, feeOnlyLosses: 0, unknownGrossLosses: 0,
}
const firstPlay = reviewFixture.plays[0]!
const position = {
  ...firstPlay, id: null, operationId: 'operation-1:long', title: 'BTC Long',
  origin: 'position' as const, executionSource: 'imported' as const,
  closedAtUtc: '2026-10-08T12:00:00Z',
  net: '-50', returnPercent: '-1', rMultiple: null, reviewed: false, local: null,
}
const mixedReport: ReviewDocument = {
  ...reviewFixture,
  metrics: mixedMetrics,
  periods: reviewFixture.periods.map(period => ({ ...period, positions: 2 })),
  groups: {
    portfolios: reviewFixture.groups.portfolios.map(group => ({ ...group, metrics: mixedMetrics })),
    accounts: reviewFixture.groups.accounts.map(group => ({ ...group, metrics: mixedMetrics })),
    assets: reviewFixture.groups.assets.map(group => ({ ...group, metrics: mixedMetrics })),
  },
  plays: [
    { ...firstPlay, operationId: null, executionSource: 'imported',
      local: { netUsd: '-2', basis: 'executions', reason: null, signDiffers: true } },
    position,
    { ...position, operationId: 'operation-2:long', closedAtUtc: '2026-10-08T16:00:00Z', net: '0', returnPercent: '0' },
  ],
  coverage: { ...reviewFixture.coverage, closedCandidates: 3 },
  positions: { mode: 'auto', included: true, minPlays: 5, plays: 1, positions: 2, omitted: 0, playWeight: '2' },
}

function setup(review = vi.fn().mockResolvedValue(mixedReport)) {
  const api = { ...createWorkspaceApi(credentialsFixture), review }
  render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn().mockResolvedValue(undefined)} />)
  return { review, user: userEvent.setup() }
}

describe('Review positions regressions (Sol review)', () => {
  it('keeps Automatic on the wire after the empty-state Reset view', async () => {
    const review = vi.fn().mockImplementation(async (query: ReviewQuery): Promise<ReviewDocument> => {
      // Match ReviewEndpoints: an omitted mode is exclude, not auto.
      const mode = query.positions ?? 'exclude'
      const included = mode === 'include' || mode === 'auto'
      if (query.portfolio === 'all' && included) return mixedReport
      return {
        ...emptyReview, options: reviewFixture.options,
        positions: { mode, included, minPlays: 5, plays: 0,
          positions: included ? 0 : null, omitted: included ? 0 : null, playWeight: '2' },
      }
    })
    const { user } = setup(review)
    await screen.findByRole('region', { name: 'Performance scorecard' })
    expect(screen.getByRole('switch', { name: 'Include imported positions' })).toBeChecked()
    await user.selectOptions(screen.getByLabelText('Portfolio'), 'unassigned')
    await user.click(await screen.findByRole('button', { name: 'Reset view' }))
    await waitFor(() => expect(review.mock.lastCall?.[0].portfolio).toBe('all'))
    expect(screen.getByText('Automatic')).toBeVisible()
    const params = new URL(reviewPath(review.mock.lastCall![0]), 'http://localhost').searchParams
    expect(params.get('positions')).toBe('auto')
  })

  it('uses only Plays in the Play-local sign-difference denominator', async () => {
    setup()
    await screen.findByRole('region', { name: 'Performance scorecard' })
    const value = screen.getByText('Sign differs by basis').closest('dt')!.nextElementSibling!
    expect(value).toHaveTextContent(/^1 of 1$/)
  })

  it('does not call three outcomes three Plays in the breakdown', async () => {
    setup()
    const table = await screen.findByRole('table', { name: 'portfolios ranked by net closed-play P&L' })
    const row = within(table).getByRole('button', { name: 'Inspect Swing trading' }).closest('tr')!
    expect(within(row).getAllByRole('cell')[0]).toHaveTextContent('1 Play · 2 positions')
  })

  it.each(['0.5', '1001'])('does not persist a prefix of invalid threshold %s', async text => {
    const user = userEvent.setup()
    render(<ReviewSettings />)
    const input = screen.getByRole('spinbutton', { name: /fewer Plays than/ })
    expect(readReviewPreferences().minPlays).toBe(5)
    await user.clear(input)
    await user.type(input, text)
    expect(input).toHaveAttribute('aria-invalid', 'true')
    await user.tab()
    expect(readReviewPreferences().minPlays).toBe(5)
    expect(localStorage.getItem(preferenceKey('review.preferences.v1'))).toBeNull()
  })

  it('rereads a threshold saved by another tab when Settings is reopened', () => {
    const view = render(<ReviewSettings />)
    expect(screen.getByRole('spinbutton', { name: /fewer Plays than/ })).toHaveValue(5)
    view.unmount()
    // Another tab of this same user saves Settings while this tab is on Overview.
    const key = preferenceKey('review.preferences.v1')
    localStorage.setItem(key, '{"minPlays":12}')
    window.dispatchEvent(new StorageEvent('storage', { key, oldValue: null,
      newValue: '{"minPlays":12}', storageArea: localStorage }))
    expect(readReviewPreferences().minPlays).toBe(12)
    render(<ReviewSettings />)
    expect(screen.getByRole('spinbutton', { name: /fewer Plays than/ })).toHaveValue(12)
  })

  it('accepts the current wire nulls and the older response without position fields', () => {
    // Current C# DTOs emit operationId:null on Plays and id/local:null on positions.
    expect(isReviewDocument(mixedReport)).toBe(true)
    // Pre-October-20 fields are absent, not null.
    expect(isReviewDocument(reviewFixture)).toBe(true)
    expect(isReviewDocument({
      ...emptyReview,
      positions: { mode: 'exclude', included: false, minPlays: 5, plays: 0,
        positions: null, omitted: null, playWeight: '2' },
    })).toBe(true)
  })
})

describe('Review positions verified behaviour (Sol review)', () => {
  it('does not repeat requests for local grouping or Definitions state', async () => {
    const { user, review } = setup()
    await screen.findByRole('region', { name: 'Performance scorecard' })
    expect(review).toHaveBeenCalledTimes(1)
    await user.click(screen.getByRole('button', { name: 'Assets' }))
    await user.click(screen.getByRole('button', { name: 'Definitions' }))
    await user.keyboard('{Escape}')
    expect(review).toHaveBeenCalledTimes(1)
  })

  it('pins an automatic exclusion decision in narrower drill-downs', async () => {
    localStorage.setItem(preferenceKey('review.preferences.v1'), '{"minPlays":0}')
    const excluded: ReviewDocument = {
      ...reviewFixture,
      positions: { mode: 'auto', included: false, minPlays: 0, plays: 3,
        positions: null, omitted: null, playWeight: '2' },
    }
    const { user, review } = setup(vi.fn().mockResolvedValue(excluded))
    await user.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    await waitFor(() => expect(review).toHaveBeenCalledTimes(2))
    expect(review).toHaveBeenLastCalledWith(expect.objectContaining({
      portfolio: reviewFixture.options.portfolios[0]!.id, positions: 'exclude', minPlays: 0, offset: 0,
    }), expect.any(AbortSignal))
  })

  it('uses Play-only R and review denominators with mixed outcomes', async () => {
    const { user } = setup()
    await screen.findByRole('region', { name: 'Performance scorecard' })
    expect(screen.getByText('1 of 1 with a complete initial stop · Small sample')).toBeVisible()
    await user.click(screen.getByRole('button', { name: '1 Play · 2 positions' }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByRole('table', { name: 'Contributing closed Plays' })
    for (const term of ['Planned risk', 'Completed reviews']) {
      expect(within(dialog).getByText(term).querySelector('strong')).toHaveTextContent('1 / 1')
    }
    const positionRow = within(dialog).getAllByText('BTC Long')[0]!.closest('tr')!
    expect(within(positionRow).queryByRole('button')).not.toBeInTheDocument()
  })

  it('adopts the migrated owner preference and never shares it with another user', () => {
    const owner = '11111111-2222-3333-4444-555555555555'
    const member = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
    localStorage.setItem(preferenceKey('review.preferences.v1'), '{"minPlays":12}')
    const { result } = renderHook(() => useReviewPreferences())
    expect(result.current[0].minPlays).toBe(12)
    act(() => scopePreferences(owner, true))
    expect(result.current[0].minPlays).toBe(12)
    expect(localStorage.getItem('vessel.review.preferences.v1')).toBeNull()
    expect(localStorage.getItem(userPreferenceKey(owner, 'review.preferences.v1'))).toBe('{"minPlays":12}')
    act(() => scopePreferences(member, false))
    expect(result.current[0].minPlays).toBe(5)
    act(() => result.current[1]({ minPlays: 8 }))
    expect(localStorage.getItem(userPreferenceKey(member, 'review.preferences.v1'))).toBe('{"minPlays":8}')
    expect(localStorage.getItem(userPreferenceKey(owner, 'review.preferences.v1'))).toBe('{"minPlays":12}')
  })

  it.each([-1, 1001, '5', 1.5, null])('rejects malformed stored threshold %s', value => {
    localStorage.setItem(preferenceKey('review.preferences.v1'), JSON.stringify({ minPlays: value }))
    expect(readReviewPreferences().minPlays).toBe(5)
  })
})
