import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import type { VenueInfo } from '@/api/system'
import { preferenceKey } from '@/lib/preference-scope'
import { readCostAssumptions } from '@/features/plays/cost-assumptions'
import { CostSettings } from './CostSettings'

afterEach(() => localStorage.clear())

const venue = (id: string, name: string) => ({ id, name, capabilities: {} }) as unknown as VenueInfo

describe('Cost assumptions settings (Phase 2, item 10a)', () => {
  it('lists no Manual venue: one manual account can stand for any broker (Sol round 1 on 10a)', () => {
    render(<CostSettings venues={[venue('hyperliquid', 'Hyperliquid'), venue('manual', 'Manual')]} />)
    expect(screen.getByRole('form', { name: 'Hyperliquid cost assumptions' })).toBeInTheDocument()
    expect(screen.queryByRole('form', { name: 'Manual cost assumptions' })).toBeNull()
  })

  it('starts blank, saves a venue\'s two percentages in this browser and clears them', async () => {
    const user = userEvent.setup()
    render(<CostSettings venues={[venue('hyperliquid', 'Hyperliquid'), venue('lighter', 'Lighter')]} />)
    const form = screen.getByRole('form', { name: 'Hyperliquid cost assumptions' })
    const fee = within(form).getByLabelText('Round-trip fee (%)')
    const slippage = within(form).getByLabelText('Slippage (%)')
    expect(fee).toHaveValue('')
    expect(within(form).getByRole('button', { name: 'Save' })).toBeDisabled()
    await user.type(fee, '0.09')
    await user.type(slippage, '0.123456')
    expect(slippage).toHaveAttribute('aria-invalid', 'true')
    expect(within(form).getByText(/from 0 to 10 with up to four decimals/)).toBeVisible()
    expect(within(form).getByRole('button', { name: 'Save' })).toBeDisabled()
    await user.clear(slippage)
    await user.type(slippage, '0.05')
    await user.click(within(form).getByRole('button', { name: 'Save' }))
    expect(within(form).getByRole('status')).toHaveTextContent('Saved in this browser.')
    expect(JSON.parse(localStorage.getItem(preferenceKey('costs.assumptions.v1'))!)).toEqual({
      hyperliquid: { feePercent: '0.09', slippagePercent: '0.05' },
    })
    await user.clear(fee)
    await user.clear(slippage)
    await user.click(within(form).getByRole('button', { name: 'Save' }))
    expect(readCostAssumptions()).toEqual({})
    expect(within(form).getByRole('status')).toHaveTextContent('Cleared.')
  })
})
