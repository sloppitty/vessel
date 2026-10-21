import { render, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { ReviewPage } from '@/features/review/ReviewPage'
import { reviewFixture } from '@/test/review-fixture'
import { createWorkspaceApi } from './workspace'

function realReportResponse() {
  vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(new Response(JSON.stringify(reviewFixture)))))
}

it('renders a legacy unlabelled real Review through the default Real client', async () => {
  realReportResponse()
  render(<ReviewPage api={createWorkspaceApi('synthetic-token')} reloadGeneration={0} onOpenPlay={vi.fn().mockResolvedValue(undefined)} />)
  expect(await screen.findByRole('button', { name: '3 Plays' })).toBeVisible()
  expect(screen.getByText('12 unassigned fills in this period.')).toBeInTheDocument()
  expect(screen.queryByRole('alert')).not.toBeInTheDocument()
})

it('shows an error rather than real results when a Paper-bound Review receives an unlabelled real report', async () => {
  realReportResponse()
  render(<ReviewPage api={createWorkspaceApi('synthetic-token', undefined, 'paper')}
    reloadGeneration={0} onOpenPlay={vi.fn().mockResolvedValue(undefined)} />)
  await waitFor(() => expect(screen.queryByText('Loading review…')).not.toBeInTheDocument())
  expect(screen.getByRole('alert')).toHaveTextContent('different trading mode')
  expect(screen.queryByRole('button', { name: '3 Plays' })).not.toBeInTheDocument()
  expect(screen.queryByText('12 unassigned fills in this period.')).not.toBeInTheDocument()
})
