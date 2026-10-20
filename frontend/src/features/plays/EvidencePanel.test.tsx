import { StrictMode, useState } from 'react'
import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { DraftEvidence } from './draft'
import { createEvidence, evidenceLimits, evidenceProblem, sniffImageType } from './evidence'
import { EvidencePanel } from './EvidencePanel'
import { PlayJournal } from './WorkspacePanels'
import { createDraft } from './draft'
import type { EvidenceImages, EvidenceImageState } from './evidence-images'

const png = () => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1])], 'setup.png', { type: 'image/png' })
const jpeg = () => new File([new Uint8Array([0xff, 0xd8, 0xff, 0xe0])], 'daily.jpg', { type: 'image/jpeg' })
const webp = () => new Blob([new TextEncoder().encode('RIFF\0\0\0\0WEBP')])

function Harness({ initial = [], onList, images }: { initial?: DraftEvidence[]; onList?: (list: DraftEvidence[]) => void; images?: EvidenceImages }) {
  const [list, setList] = useState(initial)
  return <EvidencePanel evidence={list} images={images} onChange={update => setList(current => { const next = update(current); onList?.(next); return next })} />
}

describe('Evidence helpers', () => {
  it('accepts PNG, JPEG and WebP by signature, whatever the declared type', async () => {
    expect(await sniffImageType(png())).toBe('image/png')
    expect(await sniffImageType(jpeg())).toBe('image/jpeg')
    expect(await sniffImageType(webp())).toBe('image/webp')
    expect(await sniffImageType(new File(['<svg/>'], 'x.png', { type: 'image/png' }))).toBeNull()
    expect(await evidenceProblem(new Blob([]))).toBe('The image is empty.')
    expect(await evidenceProblem(new File(['GIF89a'], 'x.gif'))).toBe('Only PNG, JPEG or WebP images can be added.')
    const huge = { size: evidenceLimits.maxBytes + 1, slice: () => png() } as unknown as Blob
    expect(await evidenceProblem(huge)).toBe('Images are limited to 10 MB.')
    expect(await evidenceProblem(png())).toBeNull()
  })
})

describe('Evidence panel', () => {
  it('adds uploaded images, rejects other files, and edits each note inline and in the viewer', async () => {
    const onList = vi.fn()
    render(<Harness onList={onList} />)
    expect(screen.getByText(/No images yet/)).toBeInTheDocument()
    const input = document.querySelector<HTMLInputElement>('input[type="file"]')!
    expect(input).toHaveAttribute('accept', 'image/png,image/jpeg,image/webp')
    await userEvent.upload(input, [png(), new File(['plain text'], 'notes.png', { type: 'image/png' }), jpeg()], { applyAccept: false })

    expect(await screen.findByRole('alert')).toHaveTextContent('notes.png: Only PNG, JPEG or WebP images can be added.')
    const cards = within(screen.getByRole('list', { name: 'Evidence images' })).getAllByRole('listitem')
    expect(cards).toHaveLength(2)
    expect(cards[0]).toHaveTextContent('Upload')
    expect(cards[0]).toHaveTextContent('setup.png')
    expect(screen.getByText('2 of 50')).toBeInTheDocument()
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    await userEvent.click(within(cards[1]!).getByRole('button', { name: 'Download image 2' }))
    expect(click.mock.contexts[0]).toHaveAttribute('download', 'daily.jpg')
    click.mockRestore()

    await userEvent.type(screen.getByRole('textbox', { name: 'Note for image 1' }), 'Range high')
    expect(onList.mock.lastCall![0][0].note).toBe('Range high')

    await userEvent.click(screen.getByRole('button', { name: 'Open Image 1: setup.png' }))
    const viewer = await screen.findByRole('dialog', { name: 'setup.png' })
    expect(within(viewer).getByRole('img', { name: 'setup.png' })).toHaveAttribute('src', 'blob:test-image')
    const note = within(viewer).getByRole('textbox', { name: 'Note for image 1' })
    expect(note).toHaveValue('Range high')
    await userEvent.type(note, ', retested')
    await userEvent.keyboard('{Escape}')
    expect(screen.getByRole('textbox', { name: 'Note for image 1' })).toHaveValue('Range high, retested')
  })

  it('removes an image only after confirmation and releases its URL', async () => {
    const items = [createEvidence('capture', png(), 'vessel-capture.png', 'BTC · Hyperliquid · 1 hour'), createEvidence('upload', jpeg(), 'daily.jpg')]
    render(<Harness initial={items} />)
    expect(screen.getAllByRole('listitem')[0]).toHaveTextContent('Capture')
    expect(screen.getAllByRole('listitem')[0]).toHaveTextContent('BTC · Hyperliquid · 1 hour')
    await userEvent.click(screen.getByRole('button', { name: 'Remove image 1' }))
    await userEvent.click(within(screen.getByRole('group', { name: 'Remove image 1?' })).getByRole('button', { name: 'Cancel' }))
    expect(screen.getAllByRole('listitem')).toHaveLength(2)
    await userEvent.click(screen.getByRole('button', { name: 'Remove image 1' }))
    await userEvent.click(within(screen.getByRole('group', { name: 'Remove image 1?' })).getByRole('button', { name: 'Remove' }))
    expect(screen.getAllByRole('listitem')).toHaveLength(1)
    expect(screen.getAllByRole('listitem')[0]).toHaveTextContent('daily.jpg')
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test-image')
  })

  it('stops at the per-play limit', async () => {
    const full = Array.from({ length: evidenceLimits.maxItems - 1 }, () => createEvidence('upload', png(), 'a.png'))
    render(<Harness initial={full} />)
    await userEvent.upload(document.querySelector<HTMLInputElement>('input[type="file"]')!, [png(), png()])
    expect(await screen.findByRole('alert')).toHaveTextContent('Only 1 more image fits; a play holds at most 50.')
    expect(screen.getAllByRole('listitem')).toHaveLength(50)
    expect(screen.getByRole('button', { name: 'Upload image' })).toBeDisabled()
  })
})

describe('Saved originals that have not loaded (D-10)', () => {
  const saved = (n: number): DraftEvidence => ({ id: `local-${n}`, serverId: `server-${n}`, savedState: { note: '', markup: null },
    source: 'upload', name: `saved-${n}.png`, sizeBytes: 2048, context: '', note: '', markup: null, addedAt: '2026-10-14T12:00:00Z' })
  const states: Record<string, EvidenceImageState> = {
    'server-1': { status: 'loading' },
    'server-2': { status: 'unavailable', message: 'The service is unavailable.' },
    'server-3': { status: 'missing' },
    'server-4': { status: 'loaded', image: png() },
  }

  it('shows loading, unavailable with Retry and missing placeholders, without download or markup', async () => {
    const retry = vi.fn()
    const onList = vi.fn()
    render(<Harness initial={[1, 2, 3, 4].map(saved)} onList={onList} images={{ state: id => states[id]!, retry }} />)
    const cards = within(screen.getByRole('list', { name: 'Evidence images' })).getAllByRole('listitem')
    expect(cards[0]).toHaveTextContent('Loading image…')
    expect(cards[1]).toHaveTextContent('Image unavailable')
    expect(cards[2]).toHaveTextContent('Missing from storage')
    expect(cards[3]!.querySelector('img')).toHaveAttribute('src', 'blob:test-image')
    for (const card of cards.slice(0, 3)) expect(card.querySelector('img')).toBeNull()
    for (const index of [1, 2, 3]) expect(screen.getByRole('button', { name: `Download image ${index}` })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Download image 4' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Open Image 2: saved-2.png' })).toHaveAccessibleDescription('Image unavailable')
    // Only an unavailable original offers Retry; a missing one does not come back by retrying.
    expect(screen.getAllByRole('button', { name: /^Retry/ })).toHaveLength(1)
    await userEvent.click(within(cards[1]!).getByRole('button', { name: 'Retry image 2' }))
    expect(retry).toHaveBeenCalledWith('server-2')

    // The note of an unloaded image stays editable.
    await userEvent.type(screen.getByRole('textbox', { name: 'Note for image 2' }), 'Breakout')
    expect(onList.mock.lastCall![0][1]).toMatchObject({ serverId: 'server-2', note: 'Breakout' })

    await userEvent.click(screen.getByRole('button', { name: 'Open Image 2: saved-2.png' }))
    const viewer = await screen.findByRole('dialog', { name: 'saved-2.png' })
    expect(viewer).toHaveTextContent('2 KB')
    expect(within(viewer).getByRole('button', { name: 'Mark up' })).toBeDisabled()
    expect(within(viewer).getByRole('button', { name: 'Download original' })).toBeDisabled()
    expect(viewer).toHaveTextContent('Download and markup need the original.')
    await userEvent.click(within(viewer).getByRole('button', { name: 'Retry image 2' }))
    expect(retry).toHaveBeenCalledTimes(2)
    await userEvent.type(within(viewer).getByRole('textbox', { name: 'Note for image 2' }), ' retest')
    expect(onList.mock.lastCall![0][1].note).toBe('Breakout retest')
    await userEvent.keyboard('{Escape}')

    // Removal is the same explicit, confirmed action as for any other image.
    await userEvent.click(screen.getByRole('button', { name: 'Remove image 3' }))
    await userEvent.click(within(screen.getByRole('group', { name: 'Remove image 3?' })).getByRole('button', { name: 'Remove' }))
    expect(onList.mock.lastCall![0].map((item: DraftEvidence) => item.serverId)).toEqual(['server-1', 'server-2', 'server-4'])
  })

  it('uses a loaded original for the viewer, download and markup', async () => {
    render(<Harness initial={[saved(4)]} images={{ state: id => states[id]!, retry: vi.fn() }} />)
    await userEvent.click(screen.getByRole('button', { name: 'Open Image 1: saved-4.png' }))
    const viewer = await screen.findByRole('dialog', { name: 'saved-4.png' })
    expect(within(viewer).getByRole('img', { name: 'saved-4.png' })).toHaveAttribute('src', 'blob:test-image')
    expect(within(viewer).getByRole('button', { name: 'Download original' })).toBeEnabled()
    await userEvent.click(within(viewer).getByRole('button', { name: 'Mark up' }))
    expect(document.querySelector('.markup-stage > img')).toHaveAttribute('src', 'blob:test-image')
  })
})

describe('Journal evidence tab', () => {
  it('opens the Evidence tab on request and shows the image count', () => {
    const notes = createDraft().notes
    const evidence = [createEvidence('capture', png(), 'c.png', 'BTC')]
    const { rerender } = render(<PlayJournal notes={notes} onChange={vi.fn()} evidence={evidence} onEvidenceChange={vi.fn()} evidenceRequest={0} />)
    expect(screen.getByRole('tab', { name: 'Thesis' })).toHaveAttribute('aria-selected', 'true')
    rerender(<PlayJournal notes={notes} onChange={vi.fn()} evidence={evidence} onEvidenceChange={vi.fn()} evidenceRequest={1} />)
    const tab = screen.getByRole('tab', { name: 'Evidence' })
    expect(tab).toHaveAttribute('aria-selected', 'true')
    expect(tab).toHaveTextContent('Evidence1')
    expect(screen.getByRole('list', { name: 'Evidence images' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Evidence' })).toBeInTheDocument()
  })
})


describe('evidence consumer cleanup', () => {
  it('keeps the thumbnail alive when its viewer closes, and revokes all leases on draft replacement/disconnect under StrictMode', async () => {
    let next = 0
    vi.mocked(URL.createObjectURL).mockClear().mockImplementation(() => `blob:panel-${++next}`)
    vi.mocked(URL.revokeObjectURL).mockClear()
    const item = createEvidence('upload', png(), 'first.png')
    const replacement = { ...item, image: jpeg(), name: 'second.jpg' }
    const change = vi.fn()
    const ui = (evidence: DraftEvidence[]) => <StrictMode><EvidencePanel evidence={evidence} onChange={change} /></StrictMode>
    const view = render(ui([item]))
    const thumbUrl = document.querySelector('.evidence-thumb img')!.getAttribute('src')!
    expect(URL.revokeObjectURL).not.toHaveBeenCalledWith(thumbUrl)
    await userEvent.click(screen.getByRole('button', { name: 'Open Image 1: first.png' }))
    const viewer = screen.getByRole('dialog')
    const viewerUrl = within(viewer).getByRole('img', { name: 'first.png' }).getAttribute('src')!
    expect(viewerUrl).not.toBe(thumbUrl)
    await userEvent.keyboard('{Escape}')
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(viewerUrl)
    expect(URL.revokeObjectURL).not.toHaveBeenCalledWith(thumbUrl)
    await userEvent.click(screen.getByRole('button', { name: 'Open Image 1: first.png' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Mark up' }))
    const markupUrl = document.querySelector('.markup-stage > img')!.getAttribute('src')!
    expect(URL.revokeObjectURL).not.toHaveBeenCalledWith(markupUrl)
    view.rerender(ui([replacement]))
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(thumbUrl)
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(markupUrl)
    view.rerender(ui([]))
    view.unmount()
    const created = vi.mocked(URL.createObjectURL).mock.results.map(result => result.value)
    // A download in an earlier test can finish its one-second cleanup here. Count only this test's leases.
    const revoked = vi.mocked(URL.revokeObjectURL).mock.calls.map(([url]) => url).filter(url => url.startsWith('blob:panel-'))
    expect(revoked.sort()).toEqual(created.sort())
    vi.mocked(URL.createObjectURL).mockImplementation(() => 'blob:test-image')
  })
})
