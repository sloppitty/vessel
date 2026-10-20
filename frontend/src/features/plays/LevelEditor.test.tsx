import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { DraftEntry } from './draft'
import { createEntry } from './draft'
import { LevelEditor } from './LevelEditor'
import { plainDecimalRule } from './numbers'

function setup({ kind = 'entry', unit = 'price', priceStep = null, direction = 'long', value = '84000.1234567890' }: {
  kind?: 'entry' | 'stop' | 'target'; unit?: 'price' | 'percent'; priceStep?: number | null; direction?: 'long' | 'short'; value?: string
} = {}) {
  const base = createEntry(0)
  const entry: DraftEntry = { ...base, price: '84533.5000000001',
    stops: [{ ...base.stops[0]!, value: kind === 'stop' ? value : '82000', unit: kind === 'stop' ? unit : 'price' }],
    targets: [{ ...base.targets[0]!, value: kind === 'target' ? value : '87000', unit: kind === 'target' ? unit : 'price' }] }
  const overlayId = `${entry.id}|${kind}${kind === 'entry' ? '' : `|${entry[kind === 'stop' ? 'stops' : 'targets'][0]!.id}`}`
  const onApply = vi.fn(), onClose = vi.fn()
  render(<LevelEditor entries={[entry]} entry={entry} overlayId={overlayId} direction={direction} leverage={10}
    priceStep={priceStep} anchor={{ x: 100, y: 100 }} onApply={onApply} onClose={onClose} />)
  const price = screen.getByRole('textbox', { name: 'Price' })
  const save = () => fireEvent.click(screen.getByRole('button', { name: 'Save' }))
  const applied = () => onApply.mock.lastCall![0] as DraftEntry
  return { entry, onApply, onClose, price, save, applied }
}

describe('in-chart level editor', () => {
  it.each(['entry', 'stop', 'target'] as const)('keeps an existing %s price exactly, including decimals beyond chart precision', kind => {
    const { entry, price, save, applied } = setup({ kind, priceStep: 10 })
    const expected = kind === 'entry' ? entry.price : entry[kind === 'stop' ? 'stops' : 'targets'][0]!.value
    expect(price).toHaveValue(expected)
    save()
    expect(applied()).toEqual(entry)
  })

  it.each(['entry', 'stop', 'target'] as const)('stores a typed %s price exactly without tick or significant-figure rounding', kind => {
    const { price, save, applied, onClose } = setup({ kind, priceStep: 10 })
    const typed = '00084533.512345678900'
    fireEvent.change(price, { target: { value: typed } })
    save()
    expect(kind === 'entry' ? applied().price : applied()[kind === 'stop' ? 'stops' : 'targets'][0]!.value).toBe(typed)
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it.each(['stop', 'target'] as const)('shows tick-rounded generated %s prices, keeps share-only edits in percent units and switches a typed price to price units', kind => {
    const { entry, price, save, applied } = setup({ kind, unit: 'percent', priceStep: 0.5, value: '5' })
    const generated = kind === 'stop' ? '84111' : '84956'
    expect(price).toHaveValue(generated)
    fireEvent.change(screen.getByRole('textbox', { name: /Share/ }), { target: { value: '37.50' } })
    save()
    const field = kind === 'stop' ? 'stops' : 'targets'
    expect(applied()[field][0]).toEqual({ ...entry[field][0], share: '37.50' })
    // Retyping a numerically equal price is still an explicit price edit, not a share-only edit.
    fireEvent.change(price, { target: { value: `${generated}.00` } })
    save()
    expect(applied()[field][0]).toMatchObject({ unit: 'price', value: `${generated}.00`, share: '37.50' })
  })

  it.each(['long', 'short'] as const)('rounds generated exits to the supplied tick for %s', direction => {
    const { entry, applied, onClose } = setup({ priceStep: 25, direction })
    fireEvent.click(screen.getByRole('button', { name: 'Add stop' }))
    const stop = applied().stops.at(-1)!
    expect(stop.unit).toBe('price')
    const expected = Math.round(Number(entry.price) * (direction === 'long' ? 0.96 : 1.04) / 25) * 25
    expect(stop.value).toBe(String(expected))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it.each(['0', '', '00.000'])('rejects price %j that is not above zero without applying it', value => {
    const { price, save, onApply, onClose } = setup()
    fireEvent.change(price, { target: { value } })
    save()
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a price above zero.')
    expect(onApply).not.toHaveBeenCalled()
    expect(onClose).not.toHaveBeenCalled()
  })

  it.each(['-1', '1e5', '1e2', '.5', '5.', 'NaN', 'Infinity', '1.2.3', '9'.repeat(400)])('rejects price %s with the server\'s plain-decimal reason', value => {
    const { price, save, onApply, onClose } = setup()
    fireEvent.change(price, { target: { value } })
    save()
    expect(screen.getByRole('alert')).toHaveTextContent(`Entry 1 price: ${plainDecimalRule}`)
    expect(onApply).not.toHaveBeenCalled()
    expect(onClose).not.toHaveBeenCalled()
  })

  it('names a stop or target and its share as the server does when their text is refused', () => {
    const { price, save, onApply } = setup({ kind: 'stop' })
    fireEvent.change(price, { target: { value: '1e2' } })
    save()
    expect(screen.getByRole('alert')).toHaveTextContent(`Entry 1 stop 1: ${plainDecimalRule}`)
    fireEvent.change(price, { target: { value: '82000' } })
    fireEvent.change(screen.getByRole('textbox', { name: /Share/ }), { target: { value: '.5' } })
    save()
    expect(screen.getByRole('alert')).toHaveTextContent(`Entry 1 stop 1 share: ${plainDecimalRule}`)
    expect(onApply).not.toHaveBeenCalled()
  })

  it('rejects invalid shares but accepts zero, and Escape closes without an edit', () => {
    const { save, onApply, onClose, applied } = setup({ kind: 'target' })
    const share = screen.getByRole('textbox', { name: /Share/ })
    fireEvent.change(share, { target: { value: '101' } })
    save()
    expect(screen.getByRole('alert')).toHaveTextContent('Share must be 0 to 100%.')
    expect(onApply).not.toHaveBeenCalled()
    fireEvent.change(share, { target: { value: '0' } })
    save()
    expect(applied().targets[0]!.share).toBe('0')
    onApply.mockClear(); onClose.mockClear()
    fireEvent.keyDown(share, { key: 'Escape' })
    expect(onApply).not.toHaveBeenCalled()
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
