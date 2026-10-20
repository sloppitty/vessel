import { useId, useRef, useState, type ChangeEvent } from 'react'
import { Download, ImageOff, ImagePlus, PenLine, RotateCw, Trash2, Upload } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'
import type { DraftEvidence } from './draft'
import { createEvidence, evidenceAccept, evidenceLimits, evidenceProblem, formatBytes } from './evidence'
import type { ImageMarkup } from '@/api/markup'
import { flattenMarkup, hasMarks, markedFileName, markupDataUrl } from './markup'
import { MarkupEditor } from './MarkupEditor'
import { useObjectUrl } from './useObjectUrl'
import { evidenceImage, type EvidenceImages, type EvidenceImageState } from './evidence-images'
import { Time } from '@/components/ui/time'

const describe = (item: DraftEvidence) => item.source === 'capture' ? `Chart capture${item.context ? ` · ${item.context}` : ''}` : item.name
const itemLabel = (item: DraftEvidence, index: number) => `Image ${index + 1}: ${describe(item)}`

/** The image with its marks layered on top; both share the same fit, so the marks stay aligned. */
function MarkedImage({ item, image, marked, alt, fit }: { item: DraftEvidence; image: Blob; marked: boolean; alt: string; fit: 'cover' | 'contain' }) {
  const url = useObjectUrl(image)
  return <span className="marked-image" data-fit={fit}>
    <img src={url} alt={alt} />
    {marked && hasMarks(item.markup) && <img className="marked-image-overlay" src={markupDataUrl(item.markup)} alt="" data-testid="marks-overlay" />}
  </span>
}

function EvidenceMarkupEditor({ item, image, onChange, onDone }: { item: DraftEvidence; image: Blob; onChange: (markup: ImageMarkup | null) => void; onDone: () => void }) {
  const url = useObjectUrl(image)
  return url ? <MarkupEditor imageUrl={url} alt={describe(item)} markup={item.markup} onChange={onChange} onDone={onDone} /> : null
}

const placeholderText = { loading: 'Loading image…', unavailable: 'Image unavailable', missing: 'Missing from storage' } as const

/** Stands in for a saved original that has not loaded. The note, marks and Remove still work. */
function Placeholder({ state, id }: { state: Exclude<EvidenceImageState, { status: 'loaded' }>; id?: string }) {
  return <span className="evidence-placeholder" data-state={state.status}>
    {state.status !== 'loading' && <ImageOff size={16} aria-hidden="true" />}
    <span id={id}>{placeholderText[state.status]}</span>
  </span>
}

function save(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/**
 * Chart captures and uploaded images with a note each. They stay in this browser until the Play is saved. A saved
 * Play's originals load on their own after it opens; until one has, it shows its state instead of the image.
 */
export function EvidencePanel({ evidence, onChange, images }: {
  evidence: DraftEvidence[]
  /** Receives an update of the current list; uploads finish asynchronously. */
  onChange: (update: (evidence: DraftEvidence[]) => DraftEvidence[]) => void
  /** Saved originals of the open Play, by server ID. */
  images?: EvidenceImages | undefined
}) {
  const input = useRef<HTMLInputElement>(null)
  const [error, setError] = useState('')
  const [viewing, setViewing] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)
  const [view, setView] = useState<'marked' | 'original'>('marked')
  const [editing, setEditing] = useState(false)
  const id = useId()
  const viewed = evidence.find(item => item.id === viewing) ?? null
  const viewedState = viewed && evidenceImage(viewed, images)
  // The stored size is known before the original loads.
  const viewedBytes = viewedState?.status === 'loaded' ? viewedState.image.size : viewed?.sizeBytes
  const full = evidence.length >= evidenceLimits.maxItems

  const setNote = (itemId: string, note: string) => onChange(current => current.map(item => item.id === itemId ? { ...item, note } : item))
  const setMarkup = (itemId: string, markup: ImageMarkup | null) => onChange(current => current.map(item => item.id === itemId ? { ...item, markup } : item))
  const open = (itemId: string) => { setViewing(itemId); setView('marked'); setEditing(false) }
  const retry = (item: DraftEvidence) => { if (item.serverId) images?.retry(item.serverId) }
  async function download(item: DraftEvidence, image: Blob, marked: boolean) {
    if (!marked || !hasMarks(item.markup)) { save(image, item.name); return }
    const flattened = await flattenMarkup(image, item.markup).catch(() => null)
    if (flattened) save(flattened, markedFileName(item.name))
    else setError('The marked image could not be created. The original is unchanged.')
  }
  const remove = (item: DraftEvidence) => {
    onChange(current => current.filter(other => other.id !== item.id))
    setConfirming(null)
    if (viewing === item.id) setViewing(null)
  }
  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const files = [...event.target.files ?? []]
    event.target.value = ''
    const added: DraftEvidence[] = []
    const problems: string[] = []
    for (const file of files) {
      const problem = await evidenceProblem(file)
      if (problem) problems.push(`${file.name}: ${problem}`)
      else added.push(createEvidence('upload', file, file.name))
    }
    // The count limit applies to the list at the time the files are added.
    onChange(current => {
      const room = Math.max(0, evidenceLimits.maxItems - current.length)
      if (added.length > room) problems.push(`Only ${room} more ${room === 1 ? 'image fits' : 'images fit'}; a play holds at most ${evidenceLimits.maxItems}.`)
      return added.length ? [...current, ...added.slice(0, room)] : current
    })
    setError(problems.join(' '))
  }

  const noteField = (item: DraftEvidence, index: number, rows: number) => <textarea className="evidence-note" rows={rows}
    aria-label={`Note for image ${index + 1}`} value={item.note} maxLength={evidenceLimits.maxNoteLength}
    placeholder="What does this show? Add a note." onChange={event => setNote(item.id, event.target.value)} />

  return <div className="evidence-panel" aria-labelledby={`${id}-title`}>
    <div className="evidence-toolbar">
      <div>
        <strong id={`${id}-title`}>Images</strong>
        <small>{evidence.length} of {evidenceLimits.maxItems}</small>
      </div>
      <input ref={input} type="file" accept={evidenceAccept} multiple hidden onChange={event => void upload(event)} />
      <Button type="button" size="sm" variant="outline" disabled={full} title={full ? `At most ${evidenceLimits.maxItems} images` : undefined}
        onClick={() => input.current?.click()}><Upload size={13} aria-hidden="true" />Upload image</Button>
    </div>
    {error && <p className="evidence-error" role="alert">{error}</p>}
    {evidence.length === 0
      ? <div className="evidence-empty"><ImagePlus size={18} aria-hidden="true" /><p>No images yet.</p></div>
      : <ul className="evidence-list" aria-label="Evidence images">
        {evidence.map((item, index) => {
          const state = evidenceImage(item, images)
          const loaded = state.status === 'loaded' ? state.image : null
          const stateId = `${id}-state-${item.id}`
          return <li key={item.id} className="evidence-card">
            <div className="evidence-frame">
              <button type="button" className="evidence-thumb" aria-label={`Open ${itemLabel(item, index)}`} onClick={() => open(item.id)}
                aria-describedby={loaded ? undefined : stateId}>
                {state.status === 'loaded' ? <MarkedImage item={item} image={state.image} marked alt="" fit="cover" /> : <Placeholder state={state} id={stateId} />}
              </button>
              {state.status === 'unavailable' && <Button type="button" size="sm" variant="outline" className="evidence-retry"
                aria-label={`Retry image ${index + 1}`} title={state.message} onClick={() => retry(item)}><RotateCw size={13} aria-hidden="true" />Retry</Button>}
              {hasMarks(item.markup) && <span className="evidence-marked">Marked</span>}
              {confirming === item.id
                ? <div className="evidence-confirm" role="group" aria-label={`Remove image ${index + 1}?`}>
                  <span>Remove this image?</span>
                  <div>
                    <Button type="button" size="sm" variant="destructive" onClick={() => remove(item)}>Remove</Button>
                    <Button type="button" size="sm" variant="ghost" autoFocus onClick={() => setConfirming(null)}>Cancel</Button>
                  </div>
                </div>
                : <div className="evidence-actions">
                  <button type="button" className="evidence-action" aria-label={`Download image ${index + 1}`} disabled={!loaded}
                    title={!loaded ? 'Available once the original loads' : hasMarks(item.markup) ? 'Download with marks' : 'Download'}
                    onClick={() => { if (loaded) void download(item, loaded, true) }}><Download size={13} aria-hidden="true" /></button>
                  <button type="button" className="evidence-action" aria-label={`Remove image ${index + 1}`} title="Remove" onClick={() => setConfirming(item.id)}><Trash2 size={13} aria-hidden="true" /></button>
                </div>}
            </div>
            <div className="evidence-meta">
              <span className="evidence-kind" data-source={item.source}>{item.source === 'capture' ? 'Capture' : 'Upload'}</span>
              <span className="evidence-name" title={describe(item)}>{item.source === 'capture' ? item.context || 'Chart' : item.name}</span>
              <Time at={item.addedAt} style="day-time" />
            </div>
            <small className="evidence-state">{!item.serverId ? 'Unsaved image' : !item.savedState || item.note !== item.savedState.note || JSON.stringify(item.markup) !== JSON.stringify(item.savedState.markup)
              ? 'Image saved · note or marks unsaved' : 'Saved to this Play'}</small>
            {noteField(item, index, 2)}
          </li>
        })}
      </ul>}
    <Dialog open={viewed !== null} onOpenChange={next => { if (!next) { setViewing(null); setEditing(false) } }}>
      {viewed && viewedState && <DialogContent className="plays-entry-dialog evidence-dialog"
        onEscapeKeyDown={event => { if (editing) event.preventDefault() }}>
        <DialogTitle>{describe(viewed)}</DialogTitle>
        <DialogDescription>{viewed.source === 'capture' ? 'Captured' : 'Added'} <Time at={viewed.addedAt} style="day-time" />{viewedBytes === undefined ? '' : ` · ${formatBytes(viewedBytes)}`}{viewed.serverId ? '' : ' · unsaved'}</DialogDescription>
        {editing && viewedState.status === 'loaded'
          ? <EvidenceMarkupEditor item={viewed} image={viewedState.image}
            onChange={markup => setMarkup(viewed.id, markup)} onDone={() => { setEditing(false); setView('marked') }} />
          : <>
            <div className="evidence-view-bar">
              {hasMarks(viewed.markup) && viewedState.status === 'loaded'
                ? <div className="evidence-view-toggle" role="group" aria-label="Image version">
                  <button type="button" aria-pressed={view === 'marked'} onClick={() => setView('marked')}>Marked</button>
                  <button type="button" aria-pressed={view === 'original'} onClick={() => setView('original')}>Original</button>
                </div>
                : viewedState.status === 'loaded' ? null : <small className="evidence-view-note">Download and markup need the original.</small>}
              <Button type="button" size="sm" variant="outline" disabled={viewedState.status !== 'loaded'} onClick={() => setEditing(true)}>
                <PenLine size={13} aria-hidden="true" />{hasMarks(viewed.markup) ? 'Edit marks' : 'Mark up'}</Button>
              <Button type="button" size="sm" variant="ghost" disabled={viewedState.status !== 'loaded'}
                onClick={() => { if (viewedState.status === 'loaded') void download(viewed, viewedState.image, view === 'marked') }}>
                <Download size={13} aria-hidden="true" />{view === 'marked' && hasMarks(viewed.markup) ? 'Download marked' : 'Download original'}</Button>
            </div>
            <div className="evidence-dialog-image">{viewedState.status === 'loaded'
              ? <MarkedImage item={viewed} image={viewedState.image} marked={view === 'marked'} alt={`${describe(viewed)}${view === 'marked' && hasMarks(viewed.markup) ? ', with marks' : ''}`} fit="contain" />
              : <div className="evidence-dialog-placeholder"><Placeholder state={viewedState} />
                {viewedState.status === 'unavailable' && <>
                  <small>{viewedState.message}</small>
                  <Button type="button" size="sm" variant="outline" aria-label={`Retry image ${evidence.indexOf(viewed) + 1}`}
                    onClick={() => retry(viewed)}><RotateCw size={13} aria-hidden="true" />Retry</Button>
                </>}
              </div>}</div>
          </>}
        {noteField(viewed, evidence.indexOf(viewed), 3)}
      </DialogContent>}
    </Dialog>
  </div>
}

