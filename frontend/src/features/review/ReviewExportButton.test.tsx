import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import { ReviewExportButton } from './ReviewExportButton'
import { ReviewPage } from './ReviewPage'
import { reviewFixture } from '@/test/review-fixture'
import { preferenceKey } from '@/lib/preference-scope'
import { credentialsFixture } from '@/test/identity-fixture'

afterEach(() => vi.unstubAllGlobals())

describe('Review CSV download (phase 2, 5a)', () => {
  it('exports the same custom automatic-position threshold as the visible report', async () => {
    localStorage.setItem(preferenceKey('review.preferences.v1'), JSON.stringify({ minPlays: 12 }))
    const review = vi.fn().mockResolvedValue(reviewFixture)
    const reviewExport = vi.fn().mockResolvedValue({ fileName: 'review.csv', blob: new Blob(['header']) })
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    render(<ReviewPage api={{ ...createWorkspaceApi(credentialsFixture), review, reviewExport }} reloadGeneration={0} onOpenPlay={async () => {}} />)
    await screen.findByRole('region', { name: 'Performance scorecard' })
    expect(review).toHaveBeenCalledWith(expect.objectContaining({ minPlays: 12 }), expect.any(AbortSignal))
    await userEvent.setup().click(screen.getByRole('button', { name: 'Download CSV' }))
    await waitFor(() => expect(reviewExport).toHaveBeenCalledWith(expect.objectContaining({ minPlays: 12 })))
  })

  it('asks for the selection without its page and hands the file to the browser', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response('kind\r\n', { headers: {
      'content-type': 'text/csv; charset=utf-8', 'content-disposition': 'attachment; filename=vessel-review-week-20261021.csv' } }))
    vi.stubGlobal('fetch', fetch)
    // The test setup mocks createObjectURL globally.
    const created = vi.mocked(URL.createObjectURL).mockClear()
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const api = createWorkspaceApi(credentialsFixture)
    render(<ReviewExportButton api={api} query={{ period: 'week', portfolio: 'all', offset: 50, positions: 'auto', minPlays: 5 }} />)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Download CSV' }))
    const url = new URL(String(fetch.mock.calls[0]![0]), 'http://vessel.local')
    expect(url.pathname).toBe('/api/review/export')
    expect(url.searchParams.get('period')).toBe('week')
    expect(url.searchParams.has('offset')).toBe(false)
    expect(created).toHaveBeenCalledOnce()
    expect(click).toHaveBeenCalledOnce()
    expect((click.mock.contexts[0] as HTMLAnchorElement).download).toBe('vessel-review-week-20261021.csv')
    click.mockRestore()
  })

  it('says so when the download fails', async () => {
    const api = { reviewExport: vi.fn().mockRejectedValue(new Error('down')) }
    render(<ReviewExportButton api={api} query={{ period: 'month', portfolio: 'all' }} />)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Download CSV' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be downloaded')
  })
})
