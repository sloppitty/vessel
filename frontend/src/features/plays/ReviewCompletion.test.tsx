import { useRef, useState } from 'react'
import { act, screen, within, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { isPlayNote, isPlaySummary, isReviewed, type PlayNote, type PlaySummary } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { PlayLog, ReviewCompletionBar } from './ReviewCompletion'
import type { PlayLogChange, PlayLogDraft } from './log-draft'

const summary = (patch: Partial<PlaySummary>): PlaySummary => ({ ...savedPlayFixture.summary, ...patch })
const note: PlayNote = { id: '00000000-0000-4000-8000-0000000000c1', text: 'Felt rushed adding.', state: 'fomo', createdAtUtc: '2026-10-20T10:00:00Z' }

describe('review completion (phase 1, 1.2)', () => {
  it('offers Complete review once a Play has ended, and No review needed only for a cancellation', async () => {
    const onAction = vi.fn()
    const view = render(<ReviewCompletionBar summary={summary({ status: 'closed', reviewCompletion: null })} dirty={false} busy={false} onAction={onAction} />)
    expect(screen.getByText('This review is not complete yet.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'No review needed' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Complete review' }))
    expect(onAction).toHaveBeenLastCalledWith('complete')
    view.rerender(<ReviewCompletionBar summary={summary({ status: 'cancelled', reviewCompletion: null })} dirty={false} busy={false} onAction={onAction} />)
    await userEvent.click(screen.getByRole('button', { name: 'No review needed' }))
    expect(onAction).toHaveBeenLastCalledWith('not-needed')
    // A cancellation known to have filled needs its review.
    view.rerender(<ReviewCompletionBar summary={summary({ status: 'cancelled', reviewCompletion: null })} dirty={false} busy={false} filled onAction={onAction} />)
    expect(screen.queryByRole('button', { name: 'No review needed' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Complete review' })).toBeEnabled()
    // Unsaved edits are saved first.
    view.rerender(<ReviewCompletionBar summary={summary({ status: 'closed', reviewCompletion: null })} dirty busy={false} onAction={onAction} />)
    expect(screen.getByRole('button', { name: 'Complete review' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Complete review' })).toHaveAttribute('title', 'Save your changes first')
  })

  it('names how a review was completed and offers Review again', async () => {
    const onAction = vi.fn()
    const view = render(<ReviewCompletionBar summary={summary({ status: 'closed', reviewCompletion: 'completed', reviewCompletedAtUtc: '2026-10-20T10:00:00Z' })}
      dirty={false} busy={false} onAction={onAction} />)
    expect(screen.getByRole('group', { name: 'Review completion' })).toHaveTextContent('Review completed · 20 Oct 2026')
    await userEvent.click(screen.getByRole('button', { name: 'Review again' }))
    expect(onAction).toHaveBeenCalledWith('reopen')
    view.rerender(<ReviewCompletionBar summary={summary({ status: 'cancelled', reviewCompletion: 'not-needed', reviewCompletedAtUtc: '2026-10-20T10:00:00Z' })}
      dirty={false} busy={false} onAction={onAction} />)
    expect(screen.getByText('No review needed')).toBeInTheDocument()
    view.rerender(<ReviewCompletionBar summary={summary({ status: 'closed', reviewCompletion: 'legacy', reviewCompletedAtUtc: '2026-10-01T10:00:00Z' })}
      dirty={false} busy={false} onAction={onAction} />)
    expect(screen.getByText('Reviewed before completion was recorded')).toBeInTheDocument()
  })

  it('tells an Open Play\'s owner that in-trade notes belong in the Log, with no completion yet', () => {
    render(<ReviewCompletionBar summary={summary({ status: 'open', reviewCompletion: null })} dirty={false} busy={false} onAction={vi.fn()} />)
    expect(screen.getByText(/Notes during the trade belong in the Log/)).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('reads reviewed from the explicit completion, and from the old rule only for a server without it', () => {
    expect(isReviewed(summary({ status: 'closed', hasReview: true, reviewedAtUtc: '2026-10-20T10:00:00Z', reviewCompletion: null }))).toBe(false)
    expect(isReviewed(summary({ status: 'closed', hasReview: false, reviewCompletion: 'not-needed', reviewCompletedAtUtc: '2026-10-20T10:00:00Z' }))).toBe(true)
    expect(isReviewed(summary({ status: 'closed', hasReview: true }))).toBe(true)
    expect(isPlaySummary(summary({ reviewCompletion: 'completed', reviewCompletedAtUtc: null }))).toBe(false)
    expect(isPlaySummary(summary({ reviewCompletion: 'reviewed' as never, reviewCompletedAtUtc: '2026-10-20T10:00:00Z' }))).toBe(false)
    expect(isPlayNote(note)).toBe(true)
    expect(isPlayNote({ ...note, state: 'anxious' })).toBe(false)
    expect(isPlayNote({ ...note, text: '  ' })).toBe(false)
  })
})

describe('in-trade log (phase 1, 1.2)', () => {
  it('lists entries in order and appends one with its state', async () => {
    const added = { ...note, id: '00000000-0000-4000-8000-0000000000c2', text: 'Calmer now.', state: 'calm' as const }
    const api = { playNotes: vi.fn().mockResolvedValue([note]), addPlayNote: vi.fn().mockResolvedValue([note, added]) } as unknown as WorkspaceApi
    const user = userEvent.setup()
    render(<PlayLog api={api} playId={savedPlayFixture.summary.id} />)
    const entries = await screen.findByRole('list')
    expect(within(entries).getByText('Felt rushed adding.')).toBeInTheDocument()
    expect(within(entries).getByText('FOMO')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Log entry'), 'Calmer now.')
    await user.selectOptions(screen.getByLabelText(/^State/), 'calm')
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    expect(api.addPlayNote).toHaveBeenCalledWith(savedPlayFixture.summary.id, { requestId: expect.any(String), text: 'Calmer now.', state: 'calm' })
    expect(await screen.findByText('Calmer now.')).toBeInTheDocument()
    expect(screen.getByLabelText('Log entry')).toHaveValue('')
    expect(screen.queryByRole('button', { name: /edit|delete/i })).not.toBeInTheDocument()
  })

  it('retries a failed entry with the same request ID, so it is never written twice', async () => {
    const add = vi.fn().mockRejectedValueOnce(new ApiError('unavailable', 'The API is unavailable.')).mockResolvedValueOnce([note])
    const api = { playNotes: vi.fn().mockResolvedValue([]), addPlayNote: add } as unknown as WorkspaceApi
    const user = userEvent.setup()
    render(<PlayLog api={api} playId={savedPlayFixture.summary.id} />)
    await screen.findByText('No entries yet.')
    await user.type(screen.getByLabelText('Log entry'), 'Felt rushed adding.')
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    await waitFor(() => expect(add).toHaveBeenCalledTimes(2))
    expect(add.mock.calls[1]![1].requestId).toBe(add.mock.calls[0]![1].requestId)
    await act(async () => {})
    expect(await screen.findByText('Felt rushed adding.')).toBeInTheDocument()
  })
})

/** The page's keeping of the log draft: it outlives the Log, so a remount reads it back. */
function KeptLog({ api, playId, mounted, onKept }: { api: WorkspaceApi; playId: string; mounted: boolean; onKept: (draft: PlayLogDraft | undefined) => void }) {
  const [draft, setDraft] = useState<PlayLogDraft | undefined>(undefined)
  const latest = useRef<PlayLogDraft | undefined>(undefined)
  const onDraft = (change: PlayLogChange) => { latest.current = change(latest.current); onKept(latest.current); setDraft(latest.current) }
  return mounted ? <PlayLog api={api} playId={playId} draft={draft} onDraft={onDraft} /> : null
}

describe('a late log write after a remount (phase 1, 1.2)', () => {
  it('clears only the entry it wrote, never newer text typed since', async () => {
    let finish!: (notes: PlayNote[]) => void
    const api = { playNotes: vi.fn().mockResolvedValue([]),
      addPlayNote: vi.fn(() => new Promise<PlayNote[]>(resolve => { finish = resolve })) } as unknown as WorkspaceApi
    const kept = vi.fn()
    const user = userEvent.setup()
    const playId = savedPlayFixture.summary.id
    const { rerender } = render(<KeptLog api={api} playId={playId} mounted onKept={kept} />)
    await screen.findByText('No entries yet.')
    await user.type(screen.getByLabelText('Log entry'), note.text)
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    // The journal tab changes and comes back while the write is still out.
    rerender(<KeptLog api={api} playId={playId} mounted={false} onKept={kept} />)
    rerender(<KeptLog api={api} playId={playId} mounted onKept={kept} />)
    expect(screen.getByLabelText('Log entry')).toHaveValue(note.text)
    await user.clear(screen.getByLabelText('Log entry'))
    await user.type(screen.getByLabelText('Log entry'), 'A newer thought.')
    await act(async () => finish([note]))
    expect(screen.getByLabelText('Log entry')).toHaveValue('A newer thought.')
    // The written entry's request is settled; the newer text is a new entry with its own request.
    expect(kept).toHaveBeenLastCalledWith({ playId, text: 'A newer thought.', state: '' })
  })

  it('clears the entry once its write is confirmed, when nothing newer was typed', async () => {
    let finish!: (notes: PlayNote[]) => void
    const api = { playNotes: vi.fn().mockResolvedValue([]),
      addPlayNote: vi.fn(() => new Promise<PlayNote[]>(resolve => { finish = resolve })) } as unknown as WorkspaceApi
    const kept = vi.fn()
    const user = userEvent.setup()
    const playId = savedPlayFixture.summary.id
    const { rerender } = render(<KeptLog api={api} playId={playId} mounted onKept={kept} />)
    await screen.findByText('No entries yet.')
    await user.type(screen.getByLabelText('Log entry'), note.text)
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    rerender(<KeptLog api={api} playId={playId} mounted={false} onKept={kept} />)
    rerender(<KeptLog api={api} playId={playId} mounted onKept={kept} />)
    await act(async () => finish([note]))
    expect(screen.getByLabelText('Log entry')).toHaveValue('')
    expect(kept).toHaveBeenLastCalledWith({ playId, text: '', state: '' })
  })
})

describe('log reads and older servers (phase 1, 1.2)', () => {
  it('never lets an earlier read hide an entry a write already showed', async () => {
    let finishRead!: (notes: PlayNote[]) => void
    const api = { playNotes: vi.fn(() => new Promise<PlayNote[]>(resolve => { finishRead = resolve })),
      addPlayNote: vi.fn().mockResolvedValue([note]) } as unknown as WorkspaceApi
    const user = userEvent.setup()
    render(<PlayLog api={api} playId={note.id} />)
    await user.type(screen.getByLabelText('Log entry'), note.text)
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    expect(await screen.findByText(note.text)).toBeInTheDocument()
    await act(async () => finishRead([]))
    expect(screen.getByText(note.text)).toBeInTheDocument()
  })
  it('offers no completion action when the server reports no completion field', () => {
    const { reviewCompletion: _, reviewCompletedAtUtc: __, ...older } = summary({ status: 'closed' })
    void _; void __
    render(<ReviewCompletionBar summary={older} dirty={false} busy={false} onAction={vi.fn()} />)
    expect(screen.queryByRole('button', { name: 'Complete review' })).not.toBeInTheDocument()
    expect(screen.queryByText('This review is not complete yet.')).not.toBeInTheDocument()
  })
})
