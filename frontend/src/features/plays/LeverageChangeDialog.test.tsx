import { render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { createDraft, createEntry, type DraftExit, type PlayDraft } from './draft'
import { LeverageChangeDialog } from './LeverageChangeDialog'
import { defaultSizeUnits } from './sizing'

const level = (id: string, value: string): DraftExit => ({ id, unit: 'percent', value, share: '100' })

function show(draft: PlayDraft) {
  const view = render(<LeverageChangeDialog draft={draft} from={1} to={10} units={defaultSizeUnits}
    onKeepPrices={vi.fn()} onKeepPercentages={vi.fn()} onCancel={vi.fn()} />)
  return { dialog: within(screen.getByRole('dialog', { name: 'Change leverage from 1× to 10×?' })), unmount: view.unmount }
}

describe('leverage change dialog', () => {
  it('reads the entry price with the plan number rule, as the chart does', () => {
    const entry = { ...createEntry(0), stops: [level('s', '10')], targets: [] }
    // A priced entry resolves the % level both ways.
    const priced = show({ ...createDraft(), entries: [{ ...entry, price: '100' }] })
    expect(priced.dialog.getByRole('row', { name: /Entry 1 stop/ })).toHaveTextContent('Entry 1 stop10% · 90100% · 9010% · 99')
    priced.unmount()
    // 1e2 is not plan text: the chart draws no entry, so the dialog has no price to show either.
    const { dialog } = show({ ...createDraft(), entries: [{ ...entry, price: '1e2' }] })
    expect(dialog.getByRole('row', { name: /Entry 1 stop/ })).toHaveTextContent('Entry 1 stop10% · —100% · —10% · —')
  })

  it('shows rescaled percentages without an exponent', () => {
    const { dialog } = show({ ...createDraft(), entries: [{ ...createEntry(0), price: '100', stops: [], targets: [level('t', '10000000000000000000000')] }] })
    const row = dialog.getByRole('row', { name: /Entry 1 target/ })
    expect(row).toHaveTextContent('100000000000000000000000%')
    expect(row.textContent).not.toMatch(/e\+/)
  })
})
