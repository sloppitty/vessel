import { StrictMode } from 'react'
import { act, render, renderHook, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import { reviewPath, type ReviewDocument, type ReviewPositionsMode, type ReviewQuery } from '@/api/review'
import { emptyReview, reviewFixture } from '@/test/review-fixture'
import { systemFixture } from '@/test/system-fixture'
import { notificationFixture, notificationPreferencesFixture } from '@/test/notification-fixture'
import { preferenceKey, scopePreferences, userPreferenceKey } from '@/lib/preference-scope'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { SettingsPage } from '@/features/workspace/SettingsPage'
import type { InAppNotifications } from '@/features/notifications/useInAppNotifications'
import { ReviewPage } from './ReviewPage'
import { ReviewSettings } from './ReviewSettings'
import { readReviewPreferences, useReviewPreferences } from './reviewPreferences'

afterEach(() => vi.restoreAllMocks())

function storageChange(minPlays: number, key = preferenceKey('review.preferences.v1')) {
  const oldValue = localStorage.getItem(key)
  const newValue = JSON.stringify({ minPlays })
  localStorage.setItem(key, newValue)
  window.dispatchEvent(new StorageEvent('storage', { key, oldValue, newValue, storageArea: localStorage }))
}

function report(query: ReviewQuery, base = reviewFixture): ReviewDocument {
  const mode = query.positions ?? 'exclude'
  const minPlays = query.minPlays ?? 5
  const included = mode === 'include' || mode === 'auto' && base.metrics.count < minPlays
  return {
    ...base,
    positions: {
      mode, included, minPlays, plays: base.metrics.count, positions: included ? 0 : null,
      omitted: included ? 0 : null, playWeight: '2',
    },
  }
}

describe('Review positions round 2 regressions (Sol review)', () => {
  it('keeps render-phase preference synchronization finite and correct in Strict Mode', async () => {
    const errors = vi.spyOn(console, 'error')
    const user = userEvent.setup()
    render(<StrictMode><ReviewSettings /></StrictMode>)
    const input = screen.getByRole('spinbutton', { name: /fewer Plays than/ })
    await user.clear(input)
    await user.type(input, '12{Enter}')
    expect(input).toHaveValue(12)
    expect(readReviewPreferences().minPlays).toBe(12)
    act(() => storageChange(8))
    expect(input).toHaveValue(8)
    await user.clear(input)
    await user.type(input, '9')
    expect(readReviewPreferences().minPlays).toBe(8)
    await user.tab()
    expect(input).toHaveValue(9)
    expect(readReviewPreferences().minPlays).toBe(9)
    expect(errors).not.toHaveBeenCalled()
  })

  it('does not save invalid completed input on Enter or a following blur', async () => {
    const user = userEvent.setup()
    render(<ReviewSettings />)
    const input = screen.getByRole('spinbutton', { name: /fewer Plays than/ })
    await user.clear(input)
    await user.type(input, '1001{Enter}')
    expect(readReviewPreferences().minPlays).toBe(5)
    await user.tab()
    expect(localStorage.getItem(preferenceKey('review.preferences.v1'))).toBeNull()
  })

  it('commits Enter in the real Settings layout without submitting either sibling form', async () => {
    const user = userEvent.setup()
    const api = {
      ...createWorkspaceApi('test'),
      notificationPreferences: vi.fn().mockResolvedValue(notificationPreferencesFixture),
    }
    const inApp: InAppNotifications = {
      status: { known: false, unreadCount: 0, enabled: true, workerOnline: true, heartbeatAtUtc: null },
      handlers: { onHead: vi.fn(), onStatus: vi.fn(), onFrame: vi.fn() },
      cursor: () => null, acceptHead: vi.fn(), subscribe: () => () => {},
      markRead: vi.fn().mockResolvedValue(notificationFixture), openLink: vi.fn(),
    }
    const { container } = render(<NotificationProvider><SettingsPage system={systemFixture} api={api}
      inApp={inApp} disconnectSession={vi.fn()} onAccounts={vi.fn()} /></NotificationProvider>)
    await screen.findByRole('button', { name: 'Save notifications' })
    const submit = vi.fn()
    container.addEventListener('submit', submit, true)
    const input = screen.getByRole('spinbutton', { name: /fewer Plays than/ })
    expect(input.closest('form')).toBeNull()
    await user.clear(input)
    await user.type(input, '12{Enter}')
    expect(readReviewPreferences().minPlays).toBe(12)
    expect(submit).not.toHaveBeenCalled()
  })

  it('rereads storage on first subscriber mount even when no storage event was delivered', () => {
    const first = render(<ReviewSettings />)
    expect(screen.getByRole('spinbutton')).toHaveValue(5)
    first.unmount()
    localStorage.setItem(preferenceKey('review.preferences.v1'), '{"minPlays":19}')
    render(<ReviewSettings />)
    expect(screen.getByRole('spinbutton')).toHaveValue(19)
  })

  it('handles a storage clear and ignores a foreign user key while mounted', () => {
    const owner = '11111111-2222-3333-4444-555555555555'
    const member = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
    scopePreferences(owner, false)
    localStorage.setItem(preferenceKey('review.preferences.v1'), '{"minPlays":12}')
    const { result } = renderHook(() => useReviewPreferences())
    expect(result.current[0].minPlays).toBe(12)
    act(() => storageChange(1, userPreferenceKey(member, 'review.preferences.v1')))
    expect(result.current[0].minPlays).toBe(12)
    act(() => {
      localStorage.clear()
      window.dispatchEvent(new StorageEvent('storage', { key: null, storageArea: localStorage }))
    })
    expect(result.current[0].minPlays).toBe(5)
  })

  it('cleans up external-store subscriptions through repeated Strict Mode mounts', () => {
    const read = vi.spyOn(Storage.prototype, 'getItem')
    for (let n = 0; n < 5; n++) {
      const view = render(<StrictMode><ReviewSettings /></StrictMode>)
      view.unmount()
    }
    read.mockClear()
    act(() => {
      window.dispatchEvent(new StorageEvent('storage', {
        key: preferenceKey('review.preferences.v1'), storageArea: localStorage,
      }))
    })
    expect(read).not.toHaveBeenCalled()
  })

  it('adopts another tab threshold, aborts the old report, and ignores its late answer', async () => {
    let resolveFirst!: (value: ReviewDocument) => void
    const review = vi.fn()
      .mockImplementationOnce(() => new Promise<ReviewDocument>(resolve => { resolveFirst = resolve }))
      .mockImplementation(async (query: ReviewQuery) => report(query))
    const api = { ...createWorkspaceApi('test'), review }
    render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await waitFor(() => expect(review).toHaveBeenCalledTimes(1))
    const signal = review.mock.calls[0]![1] as AbortSignal
    act(() => storageChange(0))
    await screen.findByRole('region', { name: 'Performance scorecard' })
    expect(signal.aborted).toBe(true)
    expect(review).toHaveBeenLastCalledWith(expect.objectContaining({ positions: 'auto', minPlays: 0 }), expect.any(AbortSignal))
    expect(screen.getByRole('switch', { name: 'Include imported positions' })).not.toBeChecked()
    await act(async () => { resolveFirst(report({ period: 'month', portfolio: 'all', positions: 'include' })) })
    expect(screen.getByRole('switch', { name: 'Include imported positions' })).not.toBeChecked()
    expect(review).toHaveBeenCalledTimes(2)
  })

  it('keeps the switch, pill and actual request mode in agreement through every transition', async () => {
    const user = userEvent.setup()
    const fetch = vi.fn(async (path: string) => {
      const params = new URL(path, 'http://localhost').searchParams
      return new Response(JSON.stringify(report({
        period: 'month', portfolio: 'all', positions: params.get('positions') as ReviewPositionsMode,
        minPlays: Number(params.get('minPlays')),
      })))
    })
    vi.stubGlobal('fetch', fetch)
    render(<StrictMode><ReviewPage api={createWorkspaceApi('test')} reloadGeneration={0} onOpenPlay={vi.fn()} /></StrictMode>)
    await screen.findByRole('region', { name: 'Performance scorecard' })
    const toggle = screen.getByRole('switch', { name: 'Include imported positions' })
    expect(toggle).toBeChecked()
    expect(screen.getByText('Automatic')).toBeVisible()
    for (const mode of ['exclude', 'include'] as const) {
      await user.click(toggle)
      await waitFor(() => expect(toggle).toBeEnabled())
      expect(new URL(fetch.mock.lastCall![0], 'http://localhost').searchParams.get('positions')).toBe(mode)
      expect(toggle).toHaveProperty('checked', mode === 'include')
      expect(screen.getByText('Manual')).toBeVisible()
    }
    await user.click(screen.getByRole('button', { name: 'Use automatic' }))
    await waitFor(() => expect(toggle).toBeEnabled())
    expect(new URL(fetch.mock.lastCall![0], 'http://localhost').searchParams.get('positions')).toBe('auto')
    expect(toggle).toBeChecked()
    expect(screen.getByText('Automatic')).toBeVisible()
  })

  it.each(['auto', 'include', 'exclude'] as const)('keeps %s in both Reset requests', async mode => {
    const user = userEvent.setup()
    const review = vi.fn(async (query: ReviewQuery) => report(query,
      query.portfolio === 'unassigned' ? { ...emptyReview, options: reviewFixture.options } : reviewFixture))
    render(<ReviewPage api={{ ...createWorkspaceApi('test'), review }} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await screen.findByRole('region', { name: 'Performance scorecard' })
    if (mode !== 'auto') {
      await user.click(screen.getByRole('switch', { name: 'Include imported positions' }))
      await waitFor(() => expect(screen.getByRole('switch')).toBeEnabled())
      if (mode === 'include') {
        await user.click(screen.getByRole('switch'))
        await waitFor(() => expect(screen.getByRole('switch')).toBeEnabled())
      }
    }
    await user.selectOptions(screen.getByLabelText('Portfolio'), 'unassigned')
    await user.click(await screen.findByRole('button', { name: 'Reset view' }))
    await waitFor(() => expect(review.mock.lastCall![0].portfolio).toBe('all'))
    expect(review.mock.lastCall![0].positions).toBe(mode)
    await screen.findByRole('button', { name: 'Inspect Swing trading' })
    await user.selectOptions(screen.getByLabelText('Portfolio'), 'unassigned')
    await screen.findByRole('button', { name: 'Reset view' })
    await user.click(screen.getByRole('button', { name: 'Reset' }))
    await waitFor(() => expect(review.mock.lastCall![0].portfolio).toBe('all'))
    expect(review.mock.lastCall![0].positions).toBe(mode)
  })

  it('shows a real HTTP 409, hides old results, and retries with the same positions query', async () => {
    const user = userEvent.setup()
    const body = JSON.stringify(report({ period: 'month', portfolio: 'all', positions: 'auto', minPlays: 5 }))
    const detail = 'Plays changed while Review read imported positions. Reload Review.'
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response(body))
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: 409, detail }), { status: 409 }))
      .mockResolvedValueOnce(new Response(body))
    vi.stubGlobal('fetch', fetch)
    render(<ReviewPage api={createWorkspaceApi('test')} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await screen.findByRole('region', { name: 'Performance scorecard' })
    await user.click(screen.getByRole('button', { name: 'Reload review' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(detail)
    expect(screen.queryByRole('region', { name: 'Performance scorecard' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Retry review' }))
    await screen.findByRole('region', { name: 'Performance scorecard' })
    expect(fetch).toHaveBeenCalledTimes(3)
    expect(fetch.mock.calls.map(([path]) => path)).toEqual(Array(3).fill(
      reviewPath({ period: 'month', portfolio: 'all', positions: 'auto', minPlays: 5 })))
  })

  it('keeps the modal retry pinned after a real HTTP 409', async () => {
    const user = userEvent.setup()
    const detail = 'Plays changed while Review read imported positions. Reload Review.'
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(report({ period: 'month', portfolio: 'all', positions: 'auto' }))))
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: 409, detail }), { status: 409 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(report({ period: 'month', portfolio: 'all', positions: 'include' }))))
    vi.stubGlobal('fetch', fetch)
    render(<ReviewPage api={createWorkspaceApi('test')} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(detail)
    await user.click(within(dialog).getByRole('button', { name: 'Retry' }))
    await within(dialog).findByRole('table', { name: 'Contributing closed Plays' })
    expect(fetch.mock.calls[2]![0]).toBe(fetch.mock.calls[1]![0])
    expect(new URL(fetch.mock.lastCall![0], 'http://localhost').searchParams.get('positions')).toBe('include')
  })

  it.each([
    ['HIGHEST ASSET P&L', 'BTC'],
    ['LOWEST ASSET P&L', 'ETH'],
  ])('keeps a narrower %s drill-down pinned to automatic exclusion', async (label, instrument) => {
    const user = userEvent.setup()
    const base: ReviewDocument = {
      ...reviewFixture,
      metrics: { ...reviewFixture.metrics, count: 6, wins: 2, losses: 2, scratches: 2 },
      groups: {
        ...reviewFixture.groups,
        assets: [
          { ...reviewFixture.groups.assets[0]!, metrics: reviewFixture.metrics },
          { ...reviewFixture.groups.assets[0]!, id: 'ETH', name: 'ETH', metrics: reviewFixture.metrics },
        ],
      },
    }
    const fetch = vi.fn(async (path: string) => {
      const params = new URL(path, 'http://localhost').searchParams
      const asset = params.get('instrument')
      const query: ReviewQuery = {
        period: 'month', portfolio: 'all', ...asset ? { instrument: asset } : {},
        positions: params.get('positions') as ReviewPositionsMode, minPlays: Number(params.get('minPlays')),
      }
      return new Response(JSON.stringify(report(query, query.instrument ? reviewFixture : base)))
    })
    vi.stubGlobal('fetch', fetch)
    render(<ReviewPage api={createWorkspaceApi('test')} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await screen.findByRole('region', { name: 'Performance scorecard' })
    expect(screen.getByRole('switch')).not.toBeChecked()
    await user.click(screen.getByRole('button', { name: new RegExp(label) }))
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.getByRole('switch')).toBeEnabled())
    const sent = new URL(fetch.mock.lastCall![0], 'http://localhost').searchParams
    expect(sent.get('instrument')).toBe(instrument)
    expect.soft(sent.get('positions')).toBe('exclude')
    expect.soft(screen.getByRole('switch')).not.toBeChecked()
  })
})
