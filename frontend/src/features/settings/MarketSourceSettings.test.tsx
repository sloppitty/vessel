import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { ApiError } from '@/api/errors'
import { MarketSourceSettings } from './MarketSourceSettings'

const venues = [{ venueId: 'hyperliquid', venueName: 'Hyperliquid', available: true, sources: [
  { id: 'xyz', name: 'XYZ', markets: 120, enabled: true }, { id: 'io', name: 'EntropyIO', markets: 1, enabled: false },
] }]
const api = (list = venues) => ({ marketSources: vi.fn().mockResolvedValue(list), setMarketSource: vi.fn().mockResolvedValue(undefined) })

describe('Market sources', () => {
  it('shows each source with its markets and saves a toggle', async () => {
    const client = api(), user = userEvent.setup()
    render(<MarketSourceSettings api={client} />)
    const xyz = await screen.findByRole('checkbox', { name: /XYZ/ })
    expect(xyz).toBeChecked()
    expect(screen.getByRole('checkbox', { name: /EntropyIO/ })).not.toBeChecked()
    expect(screen.getByText('120 markets')).toBeInTheDocument()
    expect(screen.getByText('1 market')).toBeInTheDocument()
    await user.click(xyz)
    expect(client.setMarketSource).toHaveBeenCalledWith('hyperliquid', 'xyz', false)
    await waitFor(() => expect(screen.getByRole('checkbox', { name: /XYZ/ })).not.toBeChecked())
  })
  it('keeps the saved state and says so when a toggle fails', async () => {
    const client = api(), user = userEvent.setup()
    client.setMarketSource.mockRejectedValue(new ApiError('http', 'Could not save.', 502))
    render(<MarketSourceSettings api={client} />)
    await user.click(await screen.findByRole('checkbox', { name: /XYZ/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not save.')
    expect(screen.getByRole('checkbox', { name: /XYZ/ })).toBeChecked()
  })
  it('is absent when no venue has sources', async () => {
    const client = api([])
    const view = render(<MarketSourceSettings api={client} />)
    await waitFor(() => expect(client.marketSources).toHaveBeenCalled())
    await waitFor(() => expect(view.container).toBeEmptyDOMElement())
  })
  it('says when a venue could not be read', async () => {
    render(<MarketSourceSettings api={api([{ venueId: 'hyperliquid', venueName: 'Hyperliquid', available: false, sources: [] }])} />)
    expect(await screen.findByText(/could not be read just now/)).toBeInTheDocument()
  })
})
