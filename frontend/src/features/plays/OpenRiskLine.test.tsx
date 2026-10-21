import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { OpenRisk, OpenRiskPreviewRequest } from '@/api/risk'
import type { WorkspaceApi } from '@/api/workspace'
import { emptyOpenRisk } from '@/test/workspace-fixture'
import { OverviewOpenRisk } from '@/features/workspace/OverviewOpenRisk'
import { RiskSettings } from '@/features/settings/RiskSettings'
import { OpenRiskLine } from './OpenRiskLine'
import { createDraft } from './draft'

const id = (n: number) => `0000000${n}-0000-0000-0000-000000000000`
const play = (n: number, direction: 'long' | 'short', riskUsd: string) => ({ playId: id(n), title: `P${n}`, direction, status: 'planned' as const,
  accountId: id(9), riskUsd, reason: null, rounded: false, protection: null })
// G-06: three 1.25% longs on BTC, ETH and SOL against a 1,000 balance.
const risk = (limit: string | null = null): OpenRisk => ({ ...emptyOpenRisk('portfolio:' + id(8)),
  balance: { amountUsd: '1000', contributingAccountCount: 1, totalAccountCount: 1 },
  long: { knownUsd: '37.5', knownCount: 3, unknownCount: 0, rounded: false }, short: { knownUsd: '0', knownCount: 0, unknownCount: 0, rounded: false },
  plays: [play(1, 'long', '12.5'), play(2, 'long', '12.5'), play(3, 'long', '12.5')], limitPercent: limit })

const api = (document: OpenRisk, own: string | null) => ({
  openRisk: vi.fn().mockResolvedValue(document),
  openRiskPreview: vi.fn().mockImplementation((request: OpenRiskPreviewRequest) => Promise.resolve({ playId: request.playId,
    direction: request.plan.direction, riskUsd: own, reason: own === null ? 'A stop has no price.' : null, rounded: false })),
}) as unknown as WorkspaceApi

describe('open risk in capital context', () => {
  it('shows long and short of the balance with this Play\'s increment', async () => {
    render(<OpenRiskLine api={api(risk(), '10')} scope={'portfolio:' + id(8)} reference="Main balance" playId={null} accountId={id(9)}
      draft={createDraft()} reloadKey={0} />)
    const line = await screen.findByText(/this Play \+1%/)
    expect(line.closest('p')).toHaveTextContent(/^Open risk.*long 4\.75% · short 0% of Main balance — this Play \+1%$/)
  })

  it('flags a total over the owner\'s soft limit without blocking anything', async () => {
    render(<OpenRiskLine api={api(risk('4'), '10')} scope={'portfolio:' + id(8)} reference="Main balance" playId={null} accountId={id(9)}
      draft={createDraft()} reloadKey={0} />)
    expect(await screen.findByText(/over the 4% limit/)).toBeVisible()
  })

  it('replaces the saved Play and names an unknown increment instead of guessing it', async () => {
    render(<OpenRiskLine api={api(risk('4'), null)} scope={'portfolio:' + id(8)} reference="Main balance" playId={id(1)} accountId={id(9)}
      draft={createDraft()} reloadKey={0} />)
    const line = (await screen.findByText(/this Play unknown/)).closest('p')!
    expect(line).toHaveTextContent('long 2.5% · short 0% of Main balance · 1 unknown — this Play unknown (A stop has no price.)')
    expect(line).toHaveTextContent('(limit 4%)')
  })

  it('reads unavailable when open risk cannot be read', async () => {
    const failing = { openRisk: vi.fn().mockRejectedValue(new Error('down')), openRiskPreview: vi.fn().mockResolvedValue(null) } as unknown as WorkspaceApi
    render(<OpenRiskLine api={failing} scope="all" reference="balance" playId={null} accountId={id(9)} draft={createDraft()} reloadKey={0} />)
    expect(await screen.findByText(/unavailable/)).toBeVisible()
  })
})

describe('open risk freshness and coverage (Sol round 1 on #36)', () => {
  it('reads the preview again when the saved Play reloads, so new fills are counted', async () => {
    const fake = api(risk(), '10')
    const view = render(<OpenRiskLine api={fake} scope={'portfolio:' + id(8)} reference="Main balance" playId={id(1)} accountId={id(9)}
      draft={createDraft()} reloadKey={0} />)
    await screen.findByText(/this Play \+1%/)
    expect(fake.openRiskPreview).toHaveBeenCalledTimes(1)
    view.rerender(<OpenRiskLine api={fake} scope={'portfolio:' + id(8)} reference="Main balance" playId={id(1)} accountId={id(9)}
      draft={createDraft()} reloadKey={1} />)
    await waitFor(() => expect(fake.openRiskPreview).toHaveBeenCalledTimes(2))
  })

  it('says when shares divide by the balances of only some accounts', async () => {
    const partial = { ...risk(), balance: { amountUsd: '1000', contributingAccountCount: 1, totalAccountCount: 2 } }
    render(<OpenRiskLine api={api(partial, '10')} scope={'portfolio:' + id(8)} reference="Main balance" playId={null} accountId={id(9)}
      draft={createDraft()} reloadKey={0} />)
    expect((await screen.findByText(/this Play \+1%/)).closest('p')).toHaveTextContent('of Main balance (known balances of 1 of 2 accounts)')
  })

  it('counts only confirmed missing stops as not covered', async () => {
    const open = { ...play(4, 'long', '0'), status: 'open' as const, protection: 'not-covered' as const }
    const unread = { ...play(5, 'long', '0'), status: 'open' as const, protection: 'unsupported' as const }
    const document = { ...risk(), long: { ...risk().long, knownCount: 5 }, plays: [...risk().plays, open, unread], protection: { notCovered: 1, unverified: 1 } }
    render(<OverviewOpenRisk api={api(document, null)} scope={'portfolio:' + id(8)} reloadKey={0} />)
    const tile = await screen.findByTestId('overview-open-risk')
    await waitFor(() => expect(tile).toHaveTextContent('1 not covered at the venue'))
    expect(tile).toHaveTextContent('1 with unverified stops')
  })
})

describe('Overview open risk', () => {
  it('sits among the tiles with long, short and the unknown count', async () => {
    const document = { ...risk('3'), scope: 'all', short: { knownUsd: '10', knownCount: 1, unknownCount: 1, rounded: false },
      plays: [...risk().plays, play(4, 'short', '10'), { ...play(5, 'short', '0'), riskUsd: null, reason: 'An entry has no stop.' }] }
    render(<OverviewOpenRisk api={api(document, null)} scope="all" reloadKey={0} />)
    const tile = await screen.findByTestId('overview-open-risk')
    await waitFor(() => expect(tile).toHaveTextContent('3.75% · 1%'))
    expect(tile).toHaveTextContent('long · short of balance · 1 unknown')
    expect(tile).toHaveTextContent('Over the 3% limit')
  })

  it('lists Paused and Unresolved Plays as not included', async () => {
    const document = { ...risk(), scope: 'all', notIncluded: [{ reason: 'paused' as const, count: 2 }, { reason: 'unresolved' as const, count: 1 }] }
    render(<OverviewOpenRisk api={api(document, null)} scope="all" reloadKey={0} />)
    const tile = await screen.findByTestId('overview-open-risk')
    await waitFor(() => expect(tile).toHaveTextContent('2 paused not included · 1 unresolved not included'))
  })
})

describe('Risk limits in Settings', () => {
  it('saves a percentage, refuses others and clears it', async () => {
    const user = userEvent.setup()
    const settings = { riskSettings: vi.fn().mockResolvedValue({ openRiskLimitPercent: null }),
      updateRiskSettings: vi.fn().mockImplementation((value: { openRiskLimitPercent: string | null }) => Promise.resolve(value)) }
    render(<RiskSettings api={settings} />)
    const input = await screen.findByRole('textbox', { name: /Open-risk soft limit/ })
    await waitFor(() => expect(input).toBeEnabled())
    expect(input).toHaveValue('')
    await user.type(input, '150')
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('button', { name: 'Save risk limits' })).toBeDisabled()
    await user.clear(input)
    await user.type(input, '3.5')
    await user.click(screen.getByRole('button', { name: 'Save risk limits' }))
    expect(settings.updateRiskSettings).toHaveBeenLastCalledWith({ openRiskLimitPercent: '3.5' })
    expect(await screen.findByText('Open-risk limit saved.')).toBeVisible()
    await user.clear(input)
    await user.click(screen.getByRole('button', { name: 'Save risk limits' }))
    expect(settings.updateRiskSettings).toHaveBeenLastCalledWith({ openRiskLimitPercent: null })
    expect(await screen.findByText('Open-risk limit cleared.')).toBeVisible()
  })
})
