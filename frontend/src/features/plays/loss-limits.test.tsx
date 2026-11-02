import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { PlayHistory } from '@/api/plays'
import { isLossLimits, type LossLimits } from '@/api/risk'
import type { WorkspaceApi } from '@/api/workspace'
import { RiskSettings } from '@/features/settings/RiskSettings'
import { lossLimitsFixture } from '@/test/workspace-fixture'
import { LossLimitLine } from './LossLimitLine'
import { lossLimitBreach, lossLimitCheck, lossLimitsChanged, rText, useLossLimits } from './loss-limits'
import { HistoryDialog, PreflightDialog } from './PlayDialogs'
import { SuggestionsPanel } from './Suggestions'

const limits = (day: Partial<LossLimits['day']>, week: Partial<LossLimits['week']> = {}): LossLimits =>
  ({ ...lossLimitsFixture, day: { ...lossLimitsFixture.day, ...day }, week: { ...lossLimitsFixture.week, ...week } })

describe('soft loss limits (Phase 2, item 10b)', () => {
  it('checks nothing without a limit or a read, flags a reached one, and leaves Plays without R undecided', () => {
    expect(lossLimitCheck(null)).toBeNull()
    expect(lossLimitCheck({ failed: true })).toBeNull()
    expect(lossLimitCheck({ value: lossLimitsFixture })).toBeNull()
    expect(lossLimitCheck({ value: limits({ limitR: '2', netR: '-1', withR: 1 }) })).toEqual({ code: 'loss-limit', outcome: 'passed' })
    expect(lossLimitCheck({ value: limits({ limitR: '2', netR: '-1', withR: 1, withoutR: 1 }) })).toEqual({ code: 'loss-limit', outcome: 'unknown' })
    // A Play without R on a day without a limit leaves the week's limit decided.
    expect(lossLimitCheck({ value: limits({ withoutR: 1 }, { limitR: '5', netR: '-1', withR: 1 }) })).toEqual({ code: 'loss-limit', outcome: 'passed' })
    const reached = limits({ limitR: '2', netR: '-2.125', withR: 2, breached: true })
    expect(lossLimitCheck({ value: reached })).toEqual({ code: 'loss-limit', outcome: 'skipped' })
    expect(lossLimitBreach(reached)).toBe("Today's loss −2.13R reached your 2R daily limit. Nothing is blocked.")
    expect(rText('0')).toBe('0R')
    expect(isLossLimits(reached)).toBe(true)
    expect(isLossLimits(limits({ breached: true }))).toBe(false) // A breach needs a limit.
    expect(isLossLimits(limits({ netR: '-1', withR: 0 }))).toBe(false) // A loss needs Plays with R.
  })

  it('shows the line in capital context only with a limit, and the flag in Suggestions', () => {
    const view = render(<LossLimitLine load={{ value: lossLimitsFixture }} />)
    expect(screen.queryByTestId('loss-limit-line')).toBeNull()
    view.rerender(<LossLimitLine load={{ value: limits({ limitR: '2', netR: '-2', withR: 1, withoutR: 1, breached: true }, { limitR: '5', netR: '-3', withR: 2 }) }} />)
    expect(screen.getByTestId('loss-limit-line')).toHaveTextContent('today −2R of 2R (reached) · 1 without R · this week −3R of 5R')
    view.rerender(<LossLimitLine load={{ failed: true }} />)
    expect(screen.getByTestId('loss-limit-line')).toHaveTextContent('Loss limits unavailable')
    view.unmount()
    render(<SuggestionsPanel plan={null} sizing={null} status="failed" draft={{ sizingMode: 'margin', direction: 'long' }} leverage={1}
      units={{ quote: 'USDC', base: 'BTC', quantityDecimals: 4 }} onSize={vi.fn()} onLevel={vi.fn()} onLeverage={vi.fn()}
      lossLimit="Today's loss −2R reached your 2R daily limit. Nothing is blocked." />)
    expect(screen.getByRole('note')).toHaveTextContent('reached your 2R daily limit. Nothing is blocked.')
  })

  it('takes an optional cooldown note at a reached limit and keeps it in History', async () => {
    const user = userEvent.setup()
    const onPlan = vi.fn()
    const view = render(<PreflightDialog checks={[{ code: 'thesis', outcome: 'passed' }, { code: 'loss-limit', outcome: 'skipped' }]} pending={false}
      error={null} onPlan={onPlan} onClose={vi.fn()} lossLimit="Today's loss −2R reached your 2R daily limit. Nothing is blocked." />)
    expect(screen.getByRole('note')).toHaveTextContent('reached your 2R daily limit')
    await user.type(screen.getByRole('textbox', { name: 'Cooldown note (optional)' }), '  Walk away until Monday.  ')
    await user.click(screen.getByRole('button', { name: 'Plan it anyway' }))
    expect(onPlan).toHaveBeenCalledWith('Walk away until Monday.', null)
    view.unmount()
    // Without a reached limit there is no note to write.
    render(<PreflightDialog checks={[{ code: 'loss-limit', outcome: 'passed' }]} pending={false} error={null} onPlan={onPlan} onClose={vi.fn()} />)
    expect(screen.queryByRole('textbox', { name: 'Cooldown note (optional)' })).toBeNull()
    await user.click(screen.getByRole('button', { name: 'Plan it anyway' }))
    expect(onPlan).toHaveBeenLastCalledWith(null, null)
  })

  it('lists the cooldown note in the plan history', async () => {
    const history: PlayHistory = { statusChanges: [], revisions: [{ number: 1, status: 'planned', reason: '', createdAtUtc: '2026-10-09T12:00:00Z',
      plan: { direction: 'long', sizingMode: 'margin', size: '100', leverage: '1', budgetOverride: null, entries: [], notes: { thesis: '', invalidation: '', management: '', review: '' } },
      preflight: [{ code: 'loss-limit', outcome: 'skipped', note: 'Walk away until Monday.' }] }] } as unknown as PlayHistory
    render(<HistoryDialog api={{ playHistory: vi.fn().mockResolvedValue(history) } as unknown as WorkspaceApi} playId="one" onClose={vi.fn()} />)
    expect(await screen.findByText("Pre-flight: today's and this week's losses within your loss limits · not met.")).toBeInTheDocument()
    expect(screen.getByText('Cooldown note: Walk away until Monday.')).toBeInTheDocument()
  })

  it('saves the daily and weekly limits in R beside the open-risk limit', async () => {
    const user = userEvent.setup()
    const updateRiskSettings = vi.fn().mockResolvedValue({ openRiskLimitPercent: null, dailyLossLimitR: '2', weeklyLossLimitR: '5' })
    render(<RiskSettings api={{ riskSettings: vi.fn().mockResolvedValue({ openRiskLimitPercent: null, dailyLossLimitR: null, weeklyLossLimitR: null }), updateRiskSettings }} />)
    await user.type(await screen.findByRole('textbox', { name: 'Daily loss limit (R)' }), '2')
    await user.type(screen.getByRole('textbox', { name: 'Weekly loss limit (R)' }), '500')
    expect(screen.getByRole('button', { name: 'Save risk limits' })).toBeDisabled()
    await user.clear(screen.getByRole('textbox', { name: 'Weekly loss limit (R)' }))
    await user.type(screen.getByRole('textbox', { name: 'Weekly loss limit (R)' }), '5')
    await user.click(screen.getByRole('button', { name: 'Save risk limits' }))
    expect(updateRiskSettings).toHaveBeenCalledWith({ openRiskLimitPercent: null, dailyLossLimitR: '2', weeklyLossLimitR: '5' })
    expect(await screen.findByRole('status')).toHaveTextContent('Risk limits saved.')
  })

  it('reads again after a settings save, at the reporting midnight, and on returning to the tab after it (Sol round 1)', async () => {
    vi.useFakeTimers({ now: new Date('2026-10-09T23:59:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout'], shouldAdvanceTime: true })
    try {
      const lossLimits = vi.fn().mockResolvedValue(limits({ limitR: '2', toUtc: '2026-10-10T00:00:00Z' }, { toUtc: '2026-10-12T00:00:00Z' }))
      const api = { lossLimits } as unknown as WorkspaceApi
      function Probe() {
        return <LossLimitLine load={useLossLimits(api, true, 'one')} />
      }
      render(<Probe />)
      await act(async () => { await vi.advanceTimersByTimeAsync(0) })
      expect(lossLimits).toHaveBeenCalledTimes(1)
      // Settings saved elsewhere while Plays stays mounted.
      await act(async () => { lossLimitsChanged(); await vi.advanceTimersByTimeAsync(0) })
      expect(lossLimits).toHaveBeenCalledTimes(2)
      // The reporting day ends at midnight UTC here: today's loss is read again then.
      await act(async () => { await vi.advanceTimersByTimeAsync(61_000) })
      expect(lossLimits).toHaveBeenCalledTimes(3)
      // A tab coming back after the end of the day it shows reads again; one coming back before does not.
      lossLimits.mockResolvedValue(limits({ limitR: '2', toUtc: '2026-10-10T00:00:30Z' }))
      await act(async () => { lossLimitsChanged(); await vi.advanceTimersByTimeAsync(0) })
      const reads = lossLimits.mock.calls.length
      await act(async () => { document.dispatchEvent(new Event('visibilitychange')); await vi.advanceTimersByTimeAsync(0) })
      expect(lossLimits).toHaveBeenCalledTimes(reads)
      vi.setSystemTime(new Date('2026-10-10T00:05:00Z'))
      await act(async () => { document.dispatchEvent(new Event('visibilitychange')); await vi.advanceTimersByTimeAsync(0) })
      expect(lossLimits.mock.calls.length).toBeGreaterThan(reads)
    } finally {
      vi.useRealTimers()
    }
  })
})
