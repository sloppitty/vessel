import { describe, expect, it } from 'vitest'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { portfolioAllocations, sumDecimals, totalAllocation } from './overview-allocation'

const portfolio = (id: string, name: string): Portfolio => ({ tradingMode: 'real', id, name, accountCount: 0, totalValueUsd: null, valueCoverage: 'unavailable', icon: 'folder',
  balanceUsd: null, balanceCoverage: 'unavailable', availableBalanceUsd: null, availableBalanceAccountCount: 0, availableBalanceCoverage: 'unavailable', enabledAccountCount: 0 })
const account = (id: string, portfolioId: string | null, balanceUsd: string | null, isEnabled = true): BrokerAccount => ({
  ...accountFixture, id, portfolioId, name: id, accountValueUsd: balanceUsd, isEnabled, balanceUsd,
})

describe('overview allocation', () => {
  it('sums exact decimals without floating-point drift', () => {
    expect(sumDecimals(['0.1', '0.2'])).toBe('0.3')
    expect(sumDecimals(['1250.123456', '-250.123456'])).toBe('1000')
  })

  it('shares each enabled account within its portfolio and leaves missing balances out', () => {
    const [swing, unassigned] = portfolioAllocations([
      account('a', 'p', '750'), account('b', 'p', '250'), account('c', 'p', null), account('d', 'p', '999', false), account('e', null, '100'),
    ], [portfolio('p', 'Swing')])
    expect(swing!.slices.map(s => [s.name, s.share])).toEqual([['a', '75.00%'], ['b', '25.00%']])
    expect(swing!.missingCount).toBe(1)
    expect(swing!.totalUsd).toBe('1000')
    expect(unassigned!.name).toBe('Unassigned')
    const total = totalAllocation([swing!, unassigned!])
    expect(total.slices.map(s => [s.name, s.share])).toEqual([['Swing', '90.91%'], ['Unassigned', '9.09%']])
    expect(total.missingCount).toBe(1)
  })

  it('folds past six slices into Other and keeps colours on the entity, not its size', () => {
    const accounts = ['10', '20', '30', '40', '50', '60', '70'].map((v, i) => account(`x${i}`, 'p', v))
    const [group] = portfolioAllocations(accounts, [portfolio('p', 'Many')])
    expect(group!.slices).toHaveLength(6)
    expect(group!.slices.at(-1)).toMatchObject({ name: 'Other (2)', balanceUsd: '30', slot: null, scope: null })
    // Kept accounts among the first six keep their position's slot; x6 takes the slot x0 left free in Other.
    const slots = (list: BrokerAccount[]) => Object.fromEntries(portfolioAllocations(list, [portfolio('p', 'Many')])[0]!.slices.map(s => [s.id, s.slot]))
    expect(slots(accounts)).toMatchObject({ x2: 3, x3: 4, x4: 5, x5: 6, x6: 1 })
    // A balance change that reorders and refolds the slices does not repaint the accounts that stay drawn.
    accounts[6] = account('x6', 'p', '1')
    expect(slots(accounts)).toMatchObject({ x1: 2, x2: 3, x3: 4, x4: 5, x5: 6 })
    expect(group!.shares).toMatchObject({ x0: '3.57%', x1: '7.14%' })
  })

  it('keeps each entity on its slot when balances change', () => {
    const [before] = portfolioAllocations([account('a', 'p', '100'), account('b', 'p', '50'), account('c', 'p', '10')], [portfolio('p', 'P')])
    const [after] = portfolioAllocations([account('a', 'p', '1'), account('b', 'p', null), account('c', 'p', '900')], [portfolio('p', 'P')])
    expect(before!.slices.map(s => [s.id, s.slot])).toEqual([['a', 1], ['b', 2], ['c', 3]])
    expect(after!.slices.map(s => [s.id, s.slot])).toEqual([['c', 3], ['a', 1]])
  })

  it('does not draw zero or negative balances', () => {
    const [group] = portfolioAllocations([account('a', 'p', '100'), account('b', 'p', '-20'), account('c', 'p', '0')], [portfolio('p', 'P')])
    expect(group!.slices.map(s => s.share)).toEqual(['100.00%'])
    expect(group!.notDrawn.map(n => n.name)).toEqual(['b', 'c'])
  })
})
