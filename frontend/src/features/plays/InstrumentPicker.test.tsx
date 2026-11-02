import { useState } from 'react'
import { act, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type BrokerAccount, type InstrumentCatalog, type VenueInstrument, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { accountFixture, instrumentCatalogFixture } from '@/test/workspace-fixture'
import { InstrumentPicker } from './InstrumentPicker'
import { defaultPlayTitle } from './default-title'
import { assetName } from './instruments-cascade'
import { instrumentLabel, searchInstruments, useInstrumentCatalog } from './instruments'
import { credentialsFixture } from '@/test/identity-fixture'

const wallet: BrokerAccount = { ...accountFixture, venueId: 'hyperliquid' }
function api() {
  return { ...createWorkspaceApi(credentialsFixture), instruments: vi.fn().mockResolvedValue(instrumentCatalogFixture) }
}
function Harness({ account = wallet, client, onChange = vi.fn() }: {
  account?: BrokerAccount; client: WorkspaceApi; onChange?: (value: string, source: 'manual' | 'venue') => void
}) {
  const [selection, setSelection] = useState({ value: '', source: 'venue' as 'manual' | 'venue' })
  const catalog = useInstrumentCatalog(client, account)
  return <InstrumentPicker catalog={catalog} {...selection} onChange={(value, source) => {
    setSelection({ value, source }); onChange(value, source)
  }} />
}
function Fixed({ client, value }: { client: WorkspaceApi; value: string }) {
  return <InstrumentPicker catalog={useInstrumentCatalog(client, wallet)} value={value} source="venue" onChange={vi.fn()} />
}
const field = () => screen.getByRole('combobox', { name: 'Perpetual instrument' })
function deferred() {
  let resolve!: (catalog: InstrumentCatalog) => void
  const promise = new Promise<InstrumentCatalog>(accept => { resolve = accept })
  return { promise, resolve }
}

describe('Venue instrument picker', () => {
  it('uses stable unique safe IDs for punctuation and Unicode contract IDs while preserving keyboard selection', async () => {
    const client = api(), user = userEvent.setup(), onChange = vi.fn()
    const contracts = ['A B', 'A/B', 'A_B', 'BTC:USD#1', '日本']
    client.instruments.mockResolvedValue({ ...instrumentCatalogFixture,
      instruments: contracts.map(contractId => ({ ...instrumentCatalogFixture.instruments[0]!, contractId })) })
    render(<Harness client={client} onChange={onChange} />)
    await waitFor(() => expect(field()).toBeEnabled())
    await user.click(field())
    const ids = screen.getAllByRole('option').map(option => option.id)
    expect(new Set(ids).size).toBe(contracts.length)
    for (const id of ids) expect(id).toMatch(/^[a-zA-Z0-9_-]+$/)
    expect(document.getElementById(field().getAttribute('aria-activedescendant')!)).toBe(screen.getAllByRole('option')[0])
    await user.keyboard('{ArrowDown}{Enter}')
    expect(onChange).toHaveBeenLastCalledWith('A/B', 'venue')
    await user.click(field())
    expect(screen.getAllByRole('option').map(option => option.id)).toEqual(ids)
  })
  it('searches exact venue contracts as pairs without defaulting to a sample instrument', async () => {
    const client = api(), onChange = vi.fn()
    const user = userEvent.setup()
    render(<Harness client={client} onChange={onChange} />)
    await waitFor(() => expect(field()).toBeEnabled())
    expect(client.instruments).toHaveBeenCalledWith(wallet.id, expect.any(AbortSignal))
    expect(field()).toHaveValue('')
    await user.click(field())
    expect(screen.getAllByRole('option').map(option => option.textContent)).toEqual(['BTC/USDC40×', '1000PEPE/USDC10×'])
    await user.type(field(), 'pepe')
    expect(screen.getAllByRole('option').map(option => option.textContent)).toEqual(['1000PEPE/USDC10×'])
    await user.keyboard('{Enter}')
    expect(onChange).toHaveBeenLastCalledWith('1000PEPE', 'venue')
    expect(field()).toHaveValue('1000PEPE/USDC')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    // The leverage control shows the venue maximum; the picker does not repeat it.
    expect(screen.queryByText(/Max 10×/)).not.toBeInTheDocument()
  })
  it('moves through matches with the arrow keys and restores the choice on Escape', async () => {
    const client = api(), onChange = vi.fn()
    const user = userEvent.setup()
    render(<Harness client={client} onChange={onChange} />)
    await waitFor(() => expect(field()).toBeEnabled())
    await user.click(field())
    await user.keyboard('{ArrowDown}')
    expect(screen.getByRole('option', { selected: true })).toHaveTextContent('1000PEPE/USDC')
    expect(field()).toHaveAttribute('aria-activedescendant', screen.getByRole('option', { selected: true }).id)
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
    // Focus alone does not open the list.
    await user.tab()
    await user.tab({ shift: true })
    expect(field()).toHaveFocus()
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    await user.type(field(), 'xyz')
    expect(screen.getByText('No perpetual matches “xyz”.')).toBeInTheDocument()
    await user.click(document.body)
    expect(field()).toHaveValue('')
  })
  describe('cascade', () => {
    const market = (contractId: string, extra: Partial<VenueInstrument>): VenueInstrument => ({ contractId, quantityDecimals: 2, maxLeverage: 20, quoteAsset: 'USDC', ...extra })
    const builders: InstrumentCatalog = { ...instrumentCatalogFixture, instruments: [
      market('BTC', { category: 'crypto' }), market('ETH', { category: 'crypto' }),
      market('xyz:GOLD', { category: 'commodities', source: 'xyz', sourceName: 'XYZ', displayName: 'GOLD', keywords: ['xau'] }),
      market('xyz:NVDA', { category: 'stocks', source: 'xyz', sourceName: 'XYZ', displayName: 'NVDA' }),
      market('io:NVDA', { category: 'stocks', source: 'io', sourceName: 'EntropyIO', displayName: 'NVDA' }),
    ] }
    const option = (name: RegExp) => screen.getByRole('option', { name })
    const names = () => screen.getAllByRole('option').map(item => item.textContent)
    const column = (name: string) => within(screen.getByRole('group', { name }))

    it('narrows by category and source, with counts of what each choice shows', async () => {
      const client = api(), user = userEvent.setup(), onChange = vi.fn()
      client.instruments.mockResolvedValue(builders)
      render(<Harness client={client} onChange={onChange} />)
      await waitFor(() => expect(field()).toBeEnabled())
      await user.click(field())
      await user.click(column('Category').getByRole('button', { name: 'Stocks 2' }))
      expect(names()).toEqual(['NVDA/USDCXYZ20×', 'NVDA/USDCIO20×'])
      expect(column('Source').getByRole('button', { name: 'XYZ 1' })).toBeInTheDocument()
      expect(column('Source').queryByRole('button', { name: /Hyperliquid/ })).not.toBeInTheDocument()
      await user.click(column('Source').getByRole('button', { name: 'EntropyIO 1' }))
      expect(names()).toEqual(['NVDA/USDCIO20×'])
      await user.click(option(/IO/))
      expect(onChange).toHaveBeenLastCalledWith('io:NVDA', 'venue')
      expect(field()).toHaveValue('NVDA/USDC')
    })
    it('finds a market by the keyword the venue gives it', async () => {
      const client = api(), user = userEvent.setup(), onChange = vi.fn()
      client.instruments.mockResolvedValue(builders)
      render(<Harness client={client} onChange={onChange} />)
      await waitFor(() => expect(field()).toBeEnabled())
      await user.type(field(), 'xau{Enter}')
      expect(onChange).toHaveBeenLastCalledWith('xyz:GOLD', 'venue')
    })
    it('moves through categories with the side keys while the search is empty', async () => {
      const client = api(), user = userEvent.setup()
      client.instruments.mockResolvedValue(builders)
      render(<Harness client={client} />)
      await waitFor(() => expect(field()).toBeEnabled())
      await user.click(field())
      await user.keyboard('{ArrowRight}')
      expect(column('Category').getByRole('button', { name: 'Crypto 2' })).toHaveAttribute('aria-pressed', 'true')
      expect(names()).toEqual(['BTC/USDC20×', 'ETH/USDC20×'])
      await user.keyboard('{ArrowLeft}')
      expect(screen.getAllByRole('option')).toHaveLength(5)
    })
    it('leaves out columns a venue has nothing to narrow with', async () => {
      const client = api(), user = userEvent.setup()
      render(<Harness client={client} />)
      await waitFor(() => expect(field()).toBeEnabled())
      await user.click(field())
      expect(screen.queryByRole('group', { name: 'Category' })).not.toBeInTheDocument()
      expect(screen.queryByRole('group', { name: 'Source' })).not.toBeInTheDocument()
      expect(screen.getAllByRole('option')).toHaveLength(2)
    })
    it('counts a market with no label as Other beside labelled ones', async () => {
      const client = api(), user = userEvent.setup()
      client.instruments.mockResolvedValue({ ...builders, instruments: [market('RISEX', { category: 'crypto' }), market('MYSTERY', {})] })
      render(<Harness client={client} />)
      await waitFor(() => expect(field()).toBeEnabled())
      await user.click(field())
      expect(column('Category').getByRole('button', { name: 'Other 1' })).toBeInTheDocument()
      expect(screen.queryByRole('group', { name: 'Source' })).not.toBeInTheDocument()
    })
    it('tags a market the venue states is isolated-only', async () => {
      const client = api(), user = userEvent.setup()
      client.instruments.mockResolvedValue({ ...builders, instruments: [market('xyz:AMD', { displayName: 'AMD', source: 'xyz', sourceName: 'XYZ', isolatedOnly: true }), market('BTC', {})] })
      render(<Harness client={client} />)
      await waitFor(() => expect(field()).toBeEnabled())
      await user.click(field())
      expect(names()).toEqual(['AMD/USDCXYZIsolated20×', 'BTC/USDC20×'])
    })
    it('hides a disabled source from the selector but keeps a saved market on it', async () => {
      const client = api(), user = userEvent.setup()
      client.instruments.mockResolvedValue({ ...builders, disabledSources: ['xyz'] })
      render(<Fixed client={client} value="xyz:GOLD" />)
      await waitFor(() => expect(field()).toBeEnabled())
      expect(field()).toHaveValue('GOLD/USDC')
      expect(screen.getByText(/XYZ is off in Settings/)).toBeInTheDocument()
      expect(screen.queryByText(/not in the current catalogue/)).not.toBeInTheDocument()
      await user.click(field())
      expect(names()).toEqual(['BTC/USDC20×', 'ETH/USDC20×', 'NVDA/USDCIO20×'])
    })
  })
  it('names a builder contract by its asset, leaving other labels alone', () => {
    expect(assetName('xyz:GOLD')).toBe('GOLD')
    expect(assetName('BTC')).toBe('BTC')
    expect(assetName('1000PEPE')).toBe('1000PEPE')
    expect(assetName('NOT A:KEY')).toBe('NOT A:KEY')
    const now = new Date(2026, 10, 2)
    expect(defaultPlayTitle({ instrument: 'xyz:GOLD', direction: 'long' }, now)).toBe('GOLD Long · 2 Nov 2026')
    expect(defaultPlayTitle({ instrument: 'desk:ES', instrumentSource: 'manual', direction: 'long' }, now)).toBe('desk:ES Long · 2 Nov 2026')
  })
  it('labels venue contracts as pairs in lists and keeps manual labels as entered', () => {
    expect(instrumentLabel('USDC', 'BTC', 'venue')).toBe('BTC/USDC')
    expect(instrumentLabel('USDC', 'BTC-PERP', 'manual')).toBe('BTC-PERP')
    expect(instrumentLabel(null, 'ES', 'manual')).toBe('ES')
    expect(instrumentLabel(null, 'ES', 'venue')).toBe('ES')
  })
  it('ranks exact and prefix matches before other matches, keeping catalogue order', () => {
    const list = ['ETHFI', 'ETH', 'METH', 'BTC'].map(contractId => ({ contractId, quantityDecimals: 2, maxLeverage: 5, quoteAsset: 'USDC' }))
    expect(searchInstruments(list, 'eth').map(item => item.contractId)).toEqual(['ETH', 'ETHFI', 'METH'])
    expect(searchInstruments(list, 'ETH/USDC').map(item => item.contractId)).toEqual(['ETH', 'METH'])
    // Ties keep catalogue order, where the venue lists major contracts first.
    expect(searchInstruments([...list, { ...list[0]!, contractId: 'BANANA' }], 'b').map(item => item.contractId)).toEqual(['BTC', 'BANANA'])
    expect(searchInstruments(list, ' ').map(item => item.contractId)).toEqual(['ETHFI', 'ETH', 'METH', 'BTC'])
  })
  it('shows a disabled loading selector until metadata arrives', async () => {
    const request = deferred(), client = api()
    client.instruments.mockReturnValue(request.promise)
    render(<Harness client={client} />)
    expect(field()).toBeDisabled()
    expect(field()).toHaveAttribute('aria-busy', 'true')
    await act(async () => request.resolve(instrumentCatalogFixture))
    expect(field()).toBeEnabled()
  })
  it('offers safe retry and an explicit manual fallback after a failed read', async () => {
    const client = api()
    client.instruments.mockRejectedValueOnce(new ApiError('http', 'Catalogue unavailable.', 502))
    render(<Harness client={client} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Catalogue unavailable.')
    await userEvent.click(screen.getByRole('button', { name: 'Retry instruments' }))
    await waitFor(() => expect(field()).toBeEnabled())
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(client.instruments).toHaveBeenCalledTimes(2)
    await userEvent.click(screen.getByRole('button', { name: 'Enter manually' }))
    const input = screen.getByRole('textbox', { name: 'Perpetual instrument' })
    await userEvent.type(input, 'Custom contract')
    expect(input).toHaveValue('Custom contract')
    expect(screen.getByRole('button', { name: 'About manual instruments' })).toHaveAccessibleDescription('Manual label, not venue-validated.')
  })
  it('does not read a venue for manual or disabled accounts', () => {
    const client = api()
    const view = render(<Harness client={client} account={accountFixture} />)
    expect(screen.getByRole('textbox', { name: 'Perpetual instrument' })).toBeEnabled()
    view.rerender(<Harness client={client} account={{ ...wallet, isEnabled: false }} />)
    expect(client.instruments).not.toHaveBeenCalled()
  })
  it('aborts the previous account read and ignores its late response', async () => {
    const first = deferred(), second = deferred(), client = api()
    client.instruments.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const view = render(<Harness client={client} />)
    const signal = client.instruments.mock.calls[0]![1] as AbortSignal
    view.rerender(<Harness client={client} account={{ ...wallet, id: 'bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee' }} />)
    expect(signal.aborted).toBe(true)
    await act(async () => second.resolve({ ...instrumentCatalogFixture, instruments: [{ contractId: 'ETH', quantityDecimals: 4, maxLeverage: 20, quoteAsset: 'USDC' }] }))
    await act(async () => first.resolve(instrumentCatalogFixture))
    await userEvent.click(field())
    expect(screen.getAllByRole('option').map(option => option.textContent)).toEqual(['ETH/USDC20×'])
  })
  it('cancels metadata on unmount without invoking another request', () => {
    const client = api(), request = deferred()
    client.instruments.mockReturnValue(request.promise)
    const view = render(<Harness client={client} />)
    const signal = client.instruments.mock.calls[0]![1] as AbortSignal
    view.unmount()
    expect(signal.aborted).toBe(true)
    expect(client.instruments).toHaveBeenCalledTimes(1)
  })
  it('rejects a catalogue from another venue instead of mislabelling it', async () => {
    const client = api()
    client.instruments.mockResolvedValue({ ...instrumentCatalogFixture, venueId: 'manual', scope: 'manual', instruments: [] })
    render(<Harness client={client} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('does not match the selected venue')
    expect(field()).toBeDisabled()
  })
  it('keeps a removed contract visible as unverified rather than inventing its current availability', async () => {
    render(<Fixed client={api()} value="OLD" />)
    await waitFor(() => expect(field()).toBeEnabled())
    expect(screen.getByText(/OLD is not in the current catalogue/)).toBeInTheDocument()
    expect(field()).toHaveValue('OLD')
  })
})
