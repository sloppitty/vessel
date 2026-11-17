import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import type { ReviewDocument } from '@/api/review'
import { reviewFixture } from '@/test/review-fixture'
import { credentialsFixture } from '@/test/identity-fixture'
import { ReviewPage } from './ReviewPage'

function setup(report: ReviewDocument) {
  const api = { ...createWorkspaceApi(credentialsFixture), review: vi.fn().mockResolvedValue(report) }
  render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn().mockResolvedValue(undefined)} />)
  return userEvent.setup()
}
const scorecard = async () => screen.findByRole('region', { name: 'Performance scorecard' })
const card = (region: HTMLElement, label: string) => within(region).getByText(label).closest('article')!
const detail = (term: string) => screen.getByText(term).closest('div')!.querySelector('dd')!

describe('Review display honesty (#119)', () => {
  it('gives the average loss its sign and colours by the exact value: a zero drawdown and a zero gain are neutral', async () => {
    setup({ ...reviewFixture, metrics: { ...reviewFixture.metrics, averageGain: '0', averageLoss: '10', averageLossUsd: '15', drawdown: '0' } })
    const gainLoss = card(await scorecard(), 'Avg. gain / loss').querySelector('small')!
    const [gain, loss] = gainLoss.querySelectorAll('span')
    expect(gain).toHaveTextContent(/^0\.0%$/)
    expect(gain).not.toHaveClass('positive')
    expect(loss).toHaveTextContent(/^-10\.0%$/)
    expect(loss).toHaveClass('negative')
    const losing = screen.getByText('Losing Play').closest('div')!.querySelector('strong')!
    expect(losing).toHaveTextContent('-$15')
    expect(losing).toHaveClass('negative')
    expect(detail('Max. P&L drawdown')).toHaveTextContent(/^\$0$/)
    expect(detail('Max. P&L drawdown')).not.toHaveClass('negative')
  })
  it('never prints a signed zero percent: -0.04% reads 0.0%', async () => {
    const user = setup({ ...reviewFixture, metrics: { ...reviewFixture.metrics, averageGain: '0.04', averageLoss: '0.04' },
      plays: [{ ...reviewFixture.plays[0]!, returnPercent: '-0.04' }] })
    const gainLoss = card(await scorecard(), 'Avg. gain / loss').querySelector('small')!
    expect(gainLoss).toHaveTextContent(/^0\.0% \/ 0\.0%$/)
    await user.click(screen.getByRole('button', { name: '3 Plays' }))
    const table = await screen.findByRole('table', { name: 'Contributing closed Plays' })
    const row = within(table).getAllByRole('row')[1]!
    expect(within(row).getAllByRole('cell')[3]).toHaveTextContent(/^0\.0%$/)
  })
  it('keeps a tiny period net visible beside its colour instead of a green "$0"', async () => {
    setup({ ...reviewFixture, periods: reviewFixture.periods.map(p => ({ ...p, net: '0.00004' })) })
    await scorecard()
    const period = screen.getByRole('button', { name: 'Last 7 days' }).querySelector('strong')!
    expect(period).toHaveTextContent('+$0.00004')
    expect(period).toHaveClass('positive')
  })
  it('prints R as the receipt does, never as a × multiple', async () => {
    const user = setup({ ...reviewFixture, metrics: { ...reviewFixture.metrics, averageR: '1.98' },
      plays: [{ ...reviewFixture.plays[0]!, rMultiple: '1.98' }] })
    await scorecard()
    expect(detail('Estimated R expectancy (planned-stop model)')).toHaveTextContent(/^\+1\.98R/)
    await user.click(screen.getByRole('button', { name: '3 Plays' }))
    const table = await screen.findByRole('table', { name: 'Contributing closed Plays' })
    const row = within(table).getAllByRole('row')[1]!
    expect(within(row).getAllByRole('cell')[4]).toHaveTextContent(/^\+1\.98R$/)
  })
  it('labels weighted figures and counts results while imported positions are counted', async () => {
    // One Play win and two position losses: the weighted batting average is 2 / (2 + 2) = 50%.
    const metrics = { ...reviewFixture.metrics, count: 3, positions: 2, wins: 1, losses: 2, scratches: 0, batting: '0.5' }
    setup({ ...reviewFixture, metrics, periods: reviewFixture.periods.map(p => ({ ...p, positions: 2 })),
      groups: { ...reviewFixture.groups, portfolios: reviewFixture.groups.portfolios.map(g => ({ ...g, metrics })) },
      positions: { mode: 'include', included: true, minPlays: 5, plays: 1, positions: 2, omitted: 0, playWeight: '2' } })
    const region = await scorecard()
    expect(card(region, 'Weighted batting average')).toHaveTextContent('50.0%1W2L')
    expect(within(region).queryByText('Batting average')).not.toBeInTheDocument()
    expect(card(region, 'Weighted avg. gain / loss')).toBeInTheDocument()
    expect(card(region, 'Weighted expectancy per result')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Weighted average outcome' })).toBeVisible()
    expect(screen.getByText('Winning result')).toBeInTheDocument()
    expect(screen.getByText('Losing result')).toBeInTheDocument()
    const breakdown = screen.getByRole('table', { name: /ranked by net/ })
    expect(within(breakdown).getByRole('columnheader', { name: 'Weighted batting avg.' })).toBeInTheDocument()
    expect(within(breakdown).getByRole('columnheader', { name: 'Weighted avg. gain / loss' })).toBeInTheDocument()
    // The chart counts positions too, so its unit is results, not Plays.
    expect(screen.getByRole('table', { name: /Chart values/ })).toHaveTextContent('Results')
    expect(screen.getByRole('table', { name: /Chart values/ })).not.toHaveTextContent('Plays')
    expect(document.querySelector('.pr-chart-svg title')).toHaveTextContent(/1 result$/)
  })
})
