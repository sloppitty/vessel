import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { createDraft, createEntry, type PlayDraft } from './draft'
import { PositionRiskSummary } from './PositionRiskSummary'
import type { SizeUnits } from './sizing'

const units: SizeUnits = { quote: 'USDC', base: 'ETH', quantityDecimals: 4 }
const reference = { name: 'Trading account balance', amount: '1000', quote: 'USDC' }
function plan(): PlayDraft {
  const item = { ...createEntry(0), price: '100', share: '50' }
  item.stops[0]!.value = '99'
  item.targets[0]!.value = '103'
  return { ...createDraft(), sizingMode: 'quantity', size: '1', leverage: '1',
    entries: [item, { ...item, id: 'second', name: 'Entry 2' }] }
}
const value = (label: string) => screen.getByText(label, { selector: 'dt' }).nextElementSibling!

describe('PositionRiskSummary', () => {
  it('renders whole-plan estimates, named risk share, separate coverage and exclusions', () => {
    render(<PositionRiskSummary draft={plan()} units={units} riskReference={reference} />)
    expect(screen.getByRole('group', { name: 'Planned position money summary' })).toBeInTheDocument()
    expect(value('All stops')).toHaveTextContent(/^\u22121 USDC at riskFrom the planned entry$/)
    expect(value('All stops')).toHaveAttribute('data-tone', 'loss')
    expect(value('All-targets planned gain')).toHaveTextContent(/^3 USDC$/)
    expect(value('Planned R:R')).toHaveTextContent(/^1:3$/)
    expect(value('Risk / Trading account balance')).toHaveTextContent(/^0.1%$/)
    expect(value('Priced quantity')).toHaveTextContent('1 ETH')
    for (const label of ['Entry allocation', 'Stop allocation', 'Target allocation', 'Stop coverage', 'Target coverage'])
      expect(value(label)).toHaveTextContent('100% · complete')
    // One short clause states the basis; the rest of the caveat opens from its info icon.
    expect(screen.getByText('Planned, before fees, slippage and funding')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'About planned estimates' }))
      .toHaveAccessibleDescription(/Stops and targets are separate scenarios\. Actual loss can exceed planned loss\./)
    expect(screen.queryByText(/max(imum)? loss|realized|initial risk|cross margin liquidates further/i)).not.toBeInTheDocument()
  })

  it('shows known loss plus an unknown-risk tranche, not a smaller all-stops total', () => {
    const draft = plan()
    draft.entries[1]!.stops = []
    render(<PositionRiskSummary draft={draft} units={units} riskReference={reference} />)
    expect(value('All stops')).toHaveTextContent('Known \u22120.5 USDC at riskTotal unknown · from the planned entry')
    expect(value('All stops')).toHaveAttribute('data-incomplete', 'true')
    expect(value('Stop coverage')).toHaveTextContent('50% · partial')
    expect(value('Priced quantity')).toHaveTextContent('100% of full position · complete')
    expect(value('All-targets planned gain')).toHaveTextContent(/^3 USDC$/)
    expect(value('Planned R:R')).toHaveTextContent('Unknown')
    expect(value('Risk / Trading account balance')).toHaveTextContent('Unknown')
  })

  it('updates every value when prices or direction change without retaining old estimates', () => {
    const draft = plan()
    const view = render(<PositionRiskSummary draft={draft} units={units} />)
    expect(value('All stops')).toHaveTextContent(/^\u22121 USDC at risk/)
    view.rerender(<PositionRiskSummary draft={{ ...draft, direction: 'short' }} units={units} />)
    expect(value('All stops')).toHaveTextContent(/^UnknownTotal unknown$/)
    expect(value('Stop allocation')).toHaveTextContent('100% · complete')
    expect(value('Stop coverage')).toHaveTextContent('0% · partial')
    view.rerender(<PositionRiskSummary draft={{ ...draft, entries: draft.entries.map(item => ({
      ...item, stops: item.stops.map(level => ({ ...level, value: '98' })),
    })) }} units={units} />)
    expect(value('All stops')).toHaveTextContent(/^\u22122 USDC at risk/)
  })

  it('does not infer a risk reference from size or budget and hides invalid/mismatched references', () => {
    const draft = { ...plan(), budgetOverride: '1000' }
    const view = render(<PositionRiskSummary draft={draft} units={units} />)
    expect(value('Risk share')).toHaveTextContent('UnknownNeeds a named positive reference in USDC')
    for (const riskReference of [{ ...reference, amount: '0' }, { ...reference, name: '' }, { ...reference, quote: 'USD' }]) {
      view.rerender(<PositionRiskSummary draft={draft} units={units} riskReference={riskReference} />)
      expect(value('Risk share')).toHaveTextContent('Unknown')
      expect(screen.queryByText('Risk / Trading account balance')).not.toBeInTheDocument()
    }
  })

  it('uses generic size units when no venue units are supplied', () => {
    render(<PositionRiskSummary draft={plan()} />)
    expect(value('All stops')).toHaveTextContent(/^\u22121 quote units at risk/)
    expect(value('Priced quantity')).toHaveTextContent('1 units')
  })

  it('shows zero quantities without claiming a reward-to-risk ratio', () => {
    render(<PositionRiskSummary draft={{ ...plan(), size: '0' }} units={units} riskReference={reference} />)
    expect(value('All stops')).toHaveTextContent(/^0 USDC break-evenFrom the planned entry$/)
    expect(value('All-targets planned gain')).toHaveTextContent(/^0 USDC$/)
    expect(value('Risk / Trading account balance')).toHaveTextContent(/^0%$/)
    expect(value('Planned R:R')).toHaveTextContent('Unknown')
  })

  it('marks overallocation as invalid instead of displaying a normalized total', () => {
    const draft = plan()
    draft.entries[0]!.share = '75'
    render(<PositionRiskSummary draft={draft} units={units} />)
    expect(value('Entry allocation')).toHaveTextContent('125% · invalid')
    expect(value('All stops')).toHaveTextContent('UnknownInvalid allocation')
    expect(value('Priced quantity')).toHaveTextContent('Unknown')
  })

  it('never renders nonfinite values and does not round small nonzero losses to zero', () => {
    const draft = plan()
    const view = render(<PositionRiskSummary draft={{ ...draft, size: 'Infinity' }} units={units} />)
    expect(value('All stops')).toHaveTextContent('Unknown')
    expect(screen.getByRole('group')).not.toHaveTextContent(/NaN|Infinity/)
    view.rerender(<PositionRiskSummary draft={{ ...draft, size: '0.001' }} units={units} riskReference={reference} />)
    expect(value('All stops')).toHaveTextContent(/^\u2212< 0.01 USDC at risk/)
    expect(value('Risk / Trading account balance')).toHaveTextContent(/^< 0.01%$/)
  })
})

describe('PositionRiskSummary after a fill (0.5b)', () => {
  const filled = (stop: string, direction: PlayDraft['direction'] = 'long'): PlayDraft => {
    const item = { ...createEntry(0), id: 'e1', price: '100', share: '100' }
    item.stops[0]!.value = stop
    item.targets[0]!.value = direction === 'long' ? '110' : '90'
    return { ...createDraft(), direction, sizingMode: 'quantity', size: '1', leverage: '3', entries: [item] }
  }
  const fills = { filled: new Set(['e1']), averages: new Map([['e1', '100']]) }

  it('shows +5 locked in for an Open long filled at 100 with its stop at 105', () => {
    render(<PositionRiskSummary draft={filled('105')} units={units} riskReference={reference} fills={fills} />)
    expect(value('All stops')).toHaveTextContent(/^\+5 USDC locked inFrom the average fill$/)
    expect(value('All stops')).toHaveAttribute('data-tone', 'gain')
    expect(value('All stops')).toHaveAttribute('data-incomplete', 'false')
    expect(value('Stop coverage')).toHaveTextContent('100% · complete')
    expect(value('Planned R:R')).toHaveTextContent('NoneNo loss at the stops')
    expect(value('Risk / Trading account balance')).toHaveTextContent(/^0%$/)
  })

  it('shows what remains at risk, and break-even, from the average fill', () => {
    const view = render(<PositionRiskSummary draft={filled('98')} units={units} fills={fills} />)
    expect(value('All stops')).toHaveTextContent(/^−2 USDC at riskFrom the average fill$/)
    expect(value('All stops')).toHaveAttribute('data-tone', 'loss')
    view.rerender(<PositionRiskSummary draft={filled('100')} units={units} fills={fills} />)
    expect(value('All stops')).toHaveTextContent(/^0 USDC break-evenFrom the average fill$/)
    expect(value('All stops')).not.toHaveAttribute('data-tone')
    view.rerender(<PositionRiskSummary draft={filled('95', 'short')} units={units} fills={fills} />)
    expect(value('All stops')).toHaveTextContent(/^\+5 USDC locked in/)
  })

  it('does not count a stop beyond an unfilled entry', () => {
    render(<PositionRiskSummary draft={filled('105')} units={units} />)
    expect(value('All stops')).toHaveTextContent(/^UnknownTotal unknown$/)
    expect(value('Stop coverage')).toHaveTextContent('0% · partial')
  })
})
