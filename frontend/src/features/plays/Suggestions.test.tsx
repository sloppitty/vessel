import { useState } from 'react'
import { renderHook, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { SizingDocument } from '@/api/sizing'
import { ApiError } from '@/api/errors'
import type { WorkspaceApi } from '@/api/workspace'
import { accountFixture, portfolioFixture, sizingFixture } from '@/test/workspace-fixture'
import { createDraft, createExit, type DraftExit, type PlayDraft } from './draft'
import { PlayWorkspace } from './PlayWorkspace'
import { useSizing } from './useSizing'

const exit = (value: string, unit: DraftExit['unit'] = 'price'): DraftExit => ({ ...createExit('100'), value, unit })

/** One long entry at 100 with a stop at 95 and a target at 120 (R:R 1:4). */
function plan(changes: Partial<PlayDraft> = {}, stop = exit('95'), target = exit('120')) {
  const draft = createDraft()
  draft.entries = [{ ...draft.entries[0]!, price: '100', stops: [stop], targets: [target] }]
  return { ...draft, ...changes }
}

const reduced: SizingDocument = {
  ...sizingFixture,
  exposure: { level: 'half', multiplier: '0.5', reason: 'Half size after 3 losses in a row.', lossStreak: 0, winsSinceStepDown: 0 },
  limits: { ...sizingFixture.limits, effectiveRiskPercent: '0.625' },
}

// The catalogue never loads, so units are generic and the venue maximum is unknown.
const api = { instruments: () => new Promise(() => {}) } as unknown as WorkspaceApi

function Workspace({ initial, sizing = sizingFixture, balance = '10000', available = null, onDraft, onRiskChange = () => Promise.resolve(), readOnly = false }: {
  initial: PlayDraft
  sizing?: SizingDocument | null
  balance?: string | null
  available?: string | null
  onDraft?: (draft: PlayDraft) => void
  onRiskChange?: (risk: string) => Promise<void>
  readOnly?: boolean
}) {
  const account = { ...accountFixture, balanceUsd: balance, availableStablecoinNominalUsd: available }
  const [draft, setDraft] = useState({ ...initial, accountId: account.id })
  return <PlayWorkspace accounts={[account]} portfolios={[portfolioFixture]} api={api} draft={draft} onChange={next => { onDraft?.(next); setDraft(next) }}
    sizing={sizing} onRiskChange={onRiskChange} readOnly={readOnly} status={readOnly ? 'closed' : 'draft'} />
}

const field = (name: string) => screen.getByRole('spinbutton', { name })
const panel = () => screen.getByTestId('suggestions-panel')
async function openPanel(user: ReturnType<typeof userEvent.setup>) {
  await user.click(within(panel()).getByRole('button', { name: /^Suggestions/ }))
}

describe('suggestions panel', () => {
  it('sits above the workspace, collapsed, with the count and what would change', async () => {
    const user = userEvent.setup()
    render(<Workspace initial={plan()} />)
    const block = panel()
    expect(block.compareDocumentPosition(screen.getByTestId('workspace')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    const summary = within(block).getByRole('button', { name: /^Suggestions/ })
    expect(summary).toHaveAttribute('aria-expanded', 'false')
    expect(summary).toHaveTextContent('1')
    expect(summary).toHaveTextContent('Size 2,500 quote units')
    expect(screen.queryByTestId('size-suggestion')).not.toBeInTheDocument()
    await openPanel(user)
    expect(summary).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('size-suggestion')).toBeInTheDocument()
  })

  it('suggests the risk-based size with its reasoning and applies it only on Accept', async () => {
    const user = userEvent.setup()
    const onDraft = vi.fn<(draft: PlayDraft) => void>()
    render(<Workspace initial={plan()} onDraft={onDraft} />)
    // The field carries only a marker; the suggestion itself is in the panel.
    expect(field('Whole-position margin (quote units)')).toHaveAttribute('data-suggested', 'true')
    await openPanel(user)
    // 10,000 × 1.25% = 125 at risk over a 5 stop: 25 units, 2,500 margin at 1×.
    const row = screen.getByTestId('size-suggestion')
    expect(row).toHaveTextContent('2,500 quote units')
    expect(row).toHaveTextContent(/Loses 125 quote units if every stop fills: 10,000 quote units balance × 1\.25% risk/)
    expect(onDraft).not.toHaveBeenCalled()
    await user.click(within(row).getByRole('button', { name: 'Accept suggested size' }))
    expect(onDraft.mock.lastCall?.[0]).toMatchObject({ size: '2500', leverage: '1' })
    expect(field('Whole-position margin (quote units)')).toHaveValue(2500)
    expect(field('Whole-position margin (quote units)')).not.toHaveAttribute('data-suggested')
    expect(screen.queryByTestId('size-suggestion')).not.toBeInTheDocument()
    // The headline already says the plan is within its limits; no empty-state sentence repeats it.
    expect(screen.queryByTestId('suggestions-empty')).not.toBeInTheDocument()
    expect(within(panel()).getByRole('button', { name: /^Suggestions/ })).toHaveTextContent('Plan within your limits')
  })

  it('applies reduced exposure in quantity mode and says why', async () => {
    const user = userEvent.setup()
    render(<Workspace initial={plan({ sizingMode: 'quantity' })} sizing={reduced} />)
    expect(within(panel()).getByRole('button', { name: /^Suggestions/ })).toHaveTextContent('50% exposure')
    await openPanel(user)
    // 62.5 at risk over 5: 12.5 units.
    const row = screen.getByTestId('size-suggestion')
    expect(row).toHaveTextContent('12.5 units')
    expect(row).toHaveTextContent('1.25% risk × 50% exposure')
    expect(screen.getByTestId('track-record')).toHaveTextContent('Half size after 3 losses in a row.')
  })

  it('explains what is missing without a balance, and hides on read-only plays or without the record', async () => {
    const user = userEvent.setup()
    const { unmount } = render(<Workspace initial={plan()} balance={null} />)
    await openPanel(user)
    expect(screen.getByTestId('suggestions-empty')).toHaveTextContent('Choose an account with a balance')
    unmount()
    const { unmount: second } = render(<Workspace initial={plan({}, exit('80'), exit('105'))} readOnly />)
    expect(screen.queryByTestId('suggestions-panel')).not.toBeInTheDocument()
    expect(field('Entry 1 planned stop price (quote units)')).not.toHaveAttribute('data-suggested')
    second()
    render(<Workspace initial={plan()} sizing={null} />)
    expect(screen.queryByTestId('suggestions-panel')).not.toBeInTheDocument()
  })

  it('reports a size above the budget and suggests the leverage that fits', async () => {
    const user = userEvent.setup()
    const onDraft = vi.fn<(draft: PlayDraft) => void>()
    render(<Workspace initial={plan()} available="1000" onDraft={onDraft} />)
    await openPanel(user)
    const size = screen.getByTestId('size-suggestion')
    expect(size).toHaveTextContent(/needs more margin than the 1,000 quote units budget at 1×\. Raise the leverage/)
    expect(within(size).queryByRole('button')).not.toBeInTheDocument()
    // 2,500 notional in a 1,000 budget: 3×.
    const leverage = screen.getByTestId('leverage-suggestion')
    expect(leverage).toHaveTextContent('3×')
    expect(leverage).toHaveTextContent('does not fit the 1,000 quote units budget')
    expect(field('Leverage (×)')).toHaveAttribute('data-suggested', 'true')
    await user.click(within(leverage).getByRole('button', { name: 'Accept suggested leverage' }))
    expect(onDraft.mock.lastCall?.[0]).toMatchObject({ leverage: '3', size: '' })
    expect(screen.queryByTestId('leverage-suggestion')).not.toBeInTheDocument()
    // At 3× the same risk needs 833.33 margin, which fits.
    expect(screen.getByTestId('size-suggestion')).toHaveTextContent('833.33 quote units')
  })

  it.each(['prices', 'percentages'] as const)('asks once about percent levels before a suggested leverage applies, keeping %s', async keep => {
    const user = userEvent.setup()
    const onDraft = vi.fn<(draft: PlayDraft) => void>()
    render(<Workspace initial={plan({}, exit('95'), exit('20', 'percent'))} available="1000" onDraft={onDraft} />)
    await openPanel(user)
    await user.click(screen.getByRole('button', { name: 'Accept suggested leverage' }))
    const dialog = await screen.findByRole('dialog')
    expect(onDraft).not.toHaveBeenCalled()
    // Keeping prices rescales the 20% target at 1× to 60% at 3×.
    expect(screen.getAllByRole('dialog')).toHaveLength(1)
    await user.click(within(dialog).getByRole('button', { name: keep === 'prices' ? 'Keep prices' : 'Keep % and move the levels' }))
    expect(onDraft.mock.lastCall?.[0]).toMatchObject({ leverage: '3' })
    expect(onDraft.mock.lastCall?.[0].entries[0]!.targets[0]).toMatchObject({ unit: 'percent', value: keep === 'prices' ? '60' : '20' })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('stop and target suggestions', () => {
  it('suggests the maximum stop, marks the field and keeps a percent unit', async () => {
    const user = userEvent.setup()
    render(<Workspace initial={plan({ leverage: '10' }, exit('150', 'percent'))} />)
    const stop = field('Entry 1 planned stop return at 10× leverage (%)')
    expect(stop).toHaveAttribute('data-suggested', 'true')
    await openPanel(user)
    const row = screen.getByTestId('max-stop-suggestion')
    expect(row).toHaveTextContent('Entry 1 · stop')
    expect(row).toHaveTextContent('150 → 100% (≈ 90)')
    expect(row).toHaveTextContent("15% from the entry, past the 10% maximum. Minervini's absolute maximum")
    await user.click(within(row).getByRole('button', { name: 'Accept suggested Entry 1 stop' }))
    expect(stop).toHaveValue(100)
    expect(stop).not.toHaveAttribute('data-suggested')
    expect(screen.queryByTestId('max-stop-suggestion')).not.toBeInTheDocument()
  })

  it('suggests the minimum target with the reason from the record', async () => {
    const user = userEvent.setup()
    const sizing: SizingDocument = { ...sizingFixture, limits: { ...sizingFixture.limits, minRewardRisk: '3', minRewardRiskSource: 'battingAverage' },
      record: { ...sizingFixture.record, decidedPlays: 12, wins: 3, losses: 9, battingAverage: '0.25' } }
    render(<Workspace initial={plan({}, exit('95'), exit('105'))} sizing={sizing} />)
    await openPanel(user)
    const row = screen.getByTestId('min-target-suggestion')
    expect(row).toHaveTextContent('105 → 115')
    expect(row).toHaveTextContent('Winning 25% of your plays, you break even at 1:3')
    await user.click(within(row).getByRole('button', { name: 'Accept suggested Entry 1 target 1' }))
    expect(field('Entry 1 planned target 1 price (quote units)')).toHaveValue(115)
  })

  it('marks the levels in the expanded entry editor too', async () => {
    const user = userEvent.setup()
    render(<Workspace initial={plan({}, exit('80'), exit('105'))} />)
    await user.click(screen.getByRole('button', { name: 'Expand selected entry' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('spinbutton', { name: 'Entry 1 planned stop price (quote units)' })).toHaveAttribute('data-suggested', 'true')
    expect(within(dialog).getByRole('spinbutton', { name: 'Entry 1 planned target 1 price (quote units)' })).toHaveAttribute('data-suggested', 'true')
  })
})

describe('record and risk', () => {
  it('says when there are no closed plays and shows the risk and the rules', async () => {
    const user = userEvent.setup()
    render(<Workspace initial={plan()} />)
    await openPanel(user)
    const record = screen.getByTestId('track-record')
    expect(record).toHaveTextContent('No closed plays yet')
    expect(screen.getByRole('button', { name: 'Risk per trade 1.25%. Edit' })).toHaveTextContent('Risk 1.25% per play')
    expect(within(record).getByText('How these are worked out')).toBeInTheDocument()
  })

  it('shows win rate, win/loss ratio and break-even R:R', async () => {
    const user = userEvent.setup()
    const sizing: SizingDocument = { ...reduced, record: { ...reduced.record, closedPlays: 12, decidedPlays: 11, wins: 6, losses: 5, scratches: 1,
      battingAverage: '0.5454', winLossRatio: '2.1', averageGainPercent: '8.4', averageLossPercent: '4', breakEvenRewardRisk: '0.8335' } }
    render(<Workspace initial={plan()} sizing={sizing} />)
    await openPanel(user)
    const record = screen.getByTestId('track-record')
    expect(record).toHaveTextContent('Win rate54.54%6 of last 11')
    expect(record).toHaveTextContent('Win/loss ratio2.1avg +8.4% / −4%')
    expect(record).toHaveTextContent('50% exposure · Half size after 3 losses in a row.')
  })

  it('labels a record that starts from closed Activity positions and explains it in a tip', async () => {
    const user = userEvent.setup()
    const sizing: SizingDocument = { ...sizingFixture, record: { ...sizingFixture.record, source: 'activity', closedPlays: 3, decidedPlays: 3,
      wins: 2, losses: 1, battingAverage: '0.6667', winLossRatio: '2', averageGainPercent: '4', averageLossPercent: '2', breakEvenRewardRisk: '0.5',
      recent: [{ playId: null, operationId: '12345678-1234-1234-1234-123456789abc:long', accountId: accountFixture.id, title: 'BTC Long',
        instrument: 'BTC', closedAtUtc: '2026-10-12T10:00:00Z', netResultUsd: '4', returnPercent: '4', riskUsd: null, rMultiple: null,
        outcome: 'win', feesComplete: true }] } }
    render(<Workspace initial={plan()} sizing={sizing} />)
    await openPanel(user)
    const source = screen.getByTestId('record-source')
    expect(source).toHaveTextContent('From Activity')
    expect(source).toHaveTextContent('Based on 3 closed positions from Activity — no closed play with a win or loss yet.')
    const tip = within(source).getByRole('button', { name: 'About the Activity record' })
    expect(tip).toHaveAccessibleDescription(/no R multiple.*not advice/)
    expect(screen.getByTestId('track-record')).toHaveTextContent('2 of last 3 positions')
  })

  it('does not label a record from closed plays as Activity', async () => {
    const user = userEvent.setup()
    render(<Workspace initial={plan()} sizing={{ ...sizingFixture, record: { ...sizingFixture.record, source: 'plays' } }} />)
    await openPanel(user)
    expect(screen.queryByTestId('record-source')).not.toBeInTheDocument()
  })

  it('edits the risk in place, warns above 2.5% and refuses values outside 0.1 to 5%', async () => {
    const user = userEvent.setup()
    const onRiskChange = vi.fn<(risk: string) => Promise<void>>().mockResolvedValue()
    render(<Workspace initial={plan()} onRiskChange={onRiskChange} />)
    await openPanel(user)
    await user.click(screen.getByRole('button', { name: /Risk per trade/ }))
    const input = screen.getByRole('spinbutton', { name: 'Risk per trade (%)' })
    await user.clear(input)
    await user.type(input, '3')
    expect(screen.getByText('Above 2.5%')).toBeInTheDocument()
    await user.clear(input)
    await user.type(input, '6{Enter}')
    expect(screen.getByRole('alert')).toHaveTextContent('0.1 to 5%')
    expect(onRiskChange).not.toHaveBeenCalled()
    await user.clear(input)
    await user.type(input, '1.5{Enter}')
    expect(onRiskChange).toHaveBeenCalledWith('1.5')
    await waitFor(() => expect(screen.queryByRole('spinbutton', { name: 'Risk per trade (%)' })).not.toBeInTheDocument())
  })

  it('keeps the editor open with the reason when saving fails', async () => {
    const user = userEvent.setup()
    const onRiskChange = vi.fn().mockRejectedValue(new ApiError('http', 'Risk per trade must be between 0.1% and 5%.', 400))
    render(<Workspace initial={plan()} onRiskChange={onRiskChange} />)
    await openPanel(user)
    await user.click(screen.getByRole('button', { name: /Risk per trade/ }))
    await user.type(screen.getByRole('spinbutton', { name: 'Risk per trade (%)' }), '{Backspace}{Enter}')
    expect(await screen.findByRole('alert')).toHaveTextContent('between 0.1% and 5%')
    expect(onRiskChange).toHaveBeenCalledWith('1.2')
  })
})

describe('sizing document', () => {
  it('loads once and takes the document returned by a risk change', async () => {
    const updated = { ...sizingFixture, settings: { riskPercent: '1.5' } }
    const api = { sizing: vi.fn().mockResolvedValue(sizingFixture), updateSizingSettings: vi.fn().mockResolvedValue(updated) } as unknown as WorkspaceApi
    const { result, rerender } = renderHook(() => useSizing(api))
    await waitFor(() => expect(result.current.sizing).toEqual(sizingFixture))
    rerender()
    expect(api.sizing).toHaveBeenCalledOnce()
    await result.current.updateRisk('1.5')
    await waitFor(() => expect(result.current.sizing).toEqual(updated))
    expect(api.updateSizingSettings).toHaveBeenCalledWith({ riskPercent: '1.5' })
  })

  it('stays empty when the record cannot load', async () => {
    const api = { sizing: vi.fn().mockRejectedValue(new ApiError('unavailable', 'Down.')) } as unknown as WorkspaceApi
    const { result } = renderHook(() => useSizing(api))
    await waitFor(() => expect(api.sizing).toHaveBeenCalled())
    expect(result.current.sizing).toBeNull()
  })
})
