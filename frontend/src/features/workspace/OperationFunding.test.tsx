import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { isActivityFills, isActivityOperation, isActivityPage, type FundingPayment } from '@/api/activity'
import type { WorkspaceApi } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { activityFillsFixture, activityPage, operationFixture } from '@/test/activity-fixture'
import { ActivityOperations } from './ActivityOperations'

const payment: FundingPayment = {
  id: '52345678-1234-1234-1234-123456789abc', accountId: accountFixture.id, sourceId: 'venue-funding-1',
  contractId: 'BTC', venueContractId: '14', occurredAtUtc: '2026-10-12T11:00:00.001234Z',
  occurredAtUnixNanoseconds: '1791802800001234567', amount: '-0.1234567890123456789012345678', asset: 'USDG',
}
const operation = { ...operationFixture, fundingNetUsd: payment.amount, fundingPaymentCount: 1, fundingStatus: 'partial' as const,
  fundingAsOfUtc: '2026-10-12T12:00:00Z',
  coverage: ['Funding coverage is incomplete; missing funding is not zero.', 'Execution P&L funding inclusion is unverified.'] }
const api = () => ({
  activity: vi.fn().mockResolvedValue(activityPage([operation], { unassignedFundingPayments: [{ ...payment, id: '62345678-1234-1234-1234-123456789abc' }] })),
  activityFills: vi.fn().mockResolvedValue({ ...activityFillsFixture, fundingPayments: [payment] }),
}) as unknown as WorkspaceApi

describe('settled funding', () => {
  it('shows partial funding, original exact settlement and unknown total without altering execution P&L', async () => {
    const client = api()
    render(<ActivityOperations api={client} accounts={[accountFixture]} />)
    expect(await screen.findByRole('columnheader', { name: 'Funding' })).toBeInTheDocument()
    expect(screen.getByText('Partial')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Expand BTC operation' }))
    const heading = await screen.findByText('Settled funding', { selector: 'h4' })
    expect(screen.getByText('Total incl. funding').parentElement).toHaveTextContent('Unverified')
    const funding = within(heading.closest('td')!).getByRole('table', { name: 'Settled funding payments' })
    expect(within(funding).getByText('USDG')).toBeInTheDocument()
    expect(within(funding).getByText('Paid')).toBeInTheDocument()
    expect(funding.querySelector(`[title="${payment.amount}"]`)).toBeInTheDocument()
    expect(funding.querySelector('[title*="1791802800001234567"]')).toBeInTheDocument()
    const notice = screen.getByText('Funding coverage is incomplete; missing funding is not zero.')
    expect(notice).not.toBeVisible()
    expect(screen.getByRole('button', { name: 'Nominal USD' })).toHaveAccessibleDescription('Stablecoins at 1:1, with no FX/depeg valuation.')
    await userEvent.click(screen.getByText('About these numbers', { selector: 'summary' }))
    expect(notice).toBeVisible()
    expect(screen.getByText('Execution P&L funding inclusion is unverified.')).toBeVisible()
    // Coverage time and settlement time in the display zone (UTC here), naming it (phase 1, 1.6e).
    expect(screen.getByText('12 Oct 2026, 12:00 UTC')).toBeVisible()
    expect(within(funding).getByText('12 Oct 2026, 11:00:00 UTC')).toHaveAttribute('datetime', '2026-10-12T11:00:00.001234Z')
    expect(client.activityFills).toHaveBeenCalledWith(accountFixture.id, operation.id, expect.any(AbortSignal))
  })

  it('keeps unassigned funding inspectable outside operation filters and never invents an operation', async () => {
    render(<ActivityOperations api={api()} accounts={[accountFixture]} />)
    const summary = await screen.findByText('Unassigned funding', { selector: 'summary' })
    await userEvent.click(summary)
    const disclosure = summary.closest('details')!
    expect(within(disclosure).getByText('USDG')).toBeInTheDocument()
    expect(within(disclosure).getByText('Before operation filters. Not attributed to operation results.')).toBeVisible()
    expect(screen.getAllByRole('button', { name: /Expand BTC operation/ })).toHaveLength(1)
  })

  it.each([
    { amount: 0.1 }, { amount: '1e-6' }, { occurredAtUnixNanoseconds: Number('1791802800001234567') },
    { occurredAtUnixNanoseconds: '9223372036854775808' }, { accountId: 'not-owner' },
  ])('rejects malformed settlement facts %#', fields => {
    expect(isActivityFills({ ...activityFillsFixture, fundingPayments: [{ ...payment, ...fields }] }, accountFixture.id, operation.id)).toBe(false)
    expect(isActivityPage(activityPage([operation], { unassignedFundingPayments: [{ ...payment, ...fields }] as FundingPayment[] }))).toBe(false)
  })

  it('rejects an unverified total and preserves exact validated settlement and total strings', () => {
    expect(isActivityOperation({ ...operation, totalResultUsd: '5', fundingPnlBasis: 'unknown' })).toBe(false)
    expect(isActivityOperation({ ...operation, totalResultUsd: '5', fundingPnlBasis: 'excluded', fundingStatus: 'partial' })).toBe(false)
    expect(isActivityOperation({ ...operation, totalResultUsd: '5', fundingPnlBasis: 'included', fundingStatus: 'partial' })).toBe(false)
    expect(isActivityFills({ ...activityFillsFixture, fundingPayments: [payment] }, accountFixture.id, operation.id)).toBe(true)
    expect(isActivityOperation({ ...operation, totalResultUsd: '1.0000000000000000000000000001',
      fundingStatus: 'complete', fundingPnlBasis: 'excluded' })).toBe(true)
  })
})
