import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import { PlayBundleButton } from './PlayBundleButton'

const id = '11111111-1111-4111-8111-111111111111'
afterEach(() => vi.unstubAllGlobals())

describe('Play download (phase 2, 5c)', () => {
  it('asks for the saved Play in its own mode and hands the zip to the browser', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(new Blob(['zip']), { headers: {
      'content-type': 'application/zip', 'content-disposition': 'attachment; filename=vessel-play-range-11111111-20261021.zip' } }))
    vi.stubGlobal('fetch', fetch)
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    render(<PlayBundleButton api={createWorkspaceApi('synthetic-token', undefined, 'paper')} playId={id} dirty />)
    const button = screen.getByRole('button', { name: 'Download' })
    expect(button).toHaveAttribute('title', expect.stringContaining('unsaved changes are not included'))
    await userEvent.setup().click(button)
    const url = new URL(String(fetch.mock.calls[0]![0]), 'http://vessel.local')
    expect(url.pathname).toBe(`/api/plays/${id}/bundle`)
    expect(url.searchParams.get('tradingMode')).toBe('paper')
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    expect((click.mock.contexts[0] as HTMLAnchorElement).download).toBe('vessel-play-range-11111111-20261021.zip')
    click.mockRestore()
  })

  it('says so when the download fails', async () => {
    render(<PlayBundleButton api={{ playBundle: vi.fn().mockRejectedValue(new Error('down')) }} playId={id} dirty={false} />)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Download' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be downloaded')
  })
})
