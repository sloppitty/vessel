import { describe, expect, it, vi } from 'vitest'
import { createDraft, createEntry } from './draft'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { createPlaysSession, draftEvidenceFromSaved, draftFromSaved, fieldsToSave, hasDraftContent, hasUnsavedPlayWork, isDirty, loadSavedPlay, observedPlayVersion, savedState, startNewPlay, syncEvidence } from './saved'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import type { SavedEvidence } from '@/api/plays'

describe('Unsaved draft protection', () => {
  it('starts a blank editor from a clean session without retaining the saved Play', () => {
    const draft = draftFromSaved(savedPlayFixture, [])
    const next = startNewPlay({ view: 'list', draft, saved: savedState(savedPlayFixture, draft),
      observedPlay: { api: createWorkspaceApi('test'), id: savedPlayFixture.summary.id, accountId: savedPlayFixture.summary.accountId, version: 6, conflict: true } })
    expect(next.view).toBe('editor')
    expect(next.saved).toBeNull()
    expect(next.observedPlay).toBeNull()
    expect(hasDraftContent(next.draft)).toBe(false)
  })
  it('keeps the observed version apart from the baseline and scopes it to the API, Play and account', () => {
    const api = createWorkspaceApi('test')
    const draft = draftFromSaved(savedPlayFixture, [])
    const saved = savedState(savedPlayFixture, draft)
    const observedPlay = { api, id: saved.summary.id, accountId: saved.summary.accountId, version: 6, conflict: true }
    const session = { ...createPlaysSession(), draft, saved, observedPlay }
    expect(observedPlayVersion(session, api)).toBe(6)
    expect(session.saved.summary.version).toBe(savedPlayFixture.summary.version)
    expect(observedPlayVersion(session, createWorkspaceApi('new identity'))).toBe(saved.summary.version)
    expect(observedPlayVersion({ ...session, observedPlay: { ...observedPlay, id: 'other-play' } }, api)).toBe(saved.summary.version)
    expect(observedPlayVersion({ ...session, observedPlay: { ...observedPlay, accountId: 'other-account' } }, api)).toBe(saved.summary.version)
    expect(observedPlayVersion({ ...session, saved: null }, api)).toBe(0)
  })
  it('opens the existing editor when an unsaved draft or saved Play has changes', () => {
    const session = createPlaysSession()
    session.draft.budgetOverride = '123'
    expect(startNewPlay(session)).toMatchObject({ view: 'editor', draft: session.draft })
    expect(startNewPlay(session).draft).toBe(session.draft)
    const draft = draftFromSaved(savedPlayFixture, [])
    const saved = savedState(savedPlayFixture, draft)
    draft.title = 'Unsaved saved-Play edit'
    const next = startNewPlay({ view: 'list', draft, saved })
    expect(next.draft).toBe(draft)
    expect(next.saved).toBe(saved)
  })
  it('ignores generated IDs in a new blank draft', () => {
    expect(hasDraftContent(createDraft())).toBe(false)
    expect(hasDraftContent(createDraft())).toBe(false)
  })
  it.each([
    { leverage: '2' }, { direction: 'short' as const }, { sizingMode: 'quantity' as const },
    { budgetOverride: '100' }, { entries: [createEntry(0), createEntry(1)] },
    { entries: [{ ...createEntry(0), name: 'Renamed' }] },
    { entries: [{ ...createEntry(0), stops: [] }] },
  ])('protects plan-only edits %#', change => {
    expect(hasDraftContent({ ...createDraft(), ...change })).toBe(true)
  })
})


describe('in-trade log drafts as unsaved work (phase 1, 1.2)', () => {
  const draft = draftFromSaved(savedPlayFixture, [])
  const clean = { ...createPlaysSession(), view: 'editor' as const, draft, saved: savedState(savedPlayFixture, draft) }
  const playId = savedPlayFixture.summary.id
  it('protects typed text and a write not yet confirmed for the open Play', () => {
    expect(hasUnsavedPlayWork(clean)).toBe(false)
    expect(hasUnsavedPlayWork({ ...clean, log: { playId, text: '   ', state: '' } })).toBe(false)
    const typed = { ...clean, log: { playId, text: 'Moved my stop.', state: '' as const } }
    expect(hasUnsavedPlayWork(typed)).toBe(true)
    expect(startNewPlay(typed)).toEqual({ ...typed, view: 'editor' })
    // Cleared text whose write is still out may already be stored: it is kept until the answer.
    expect(hasUnsavedPlayWork({ ...clean, log: { playId, text: '', state: '', attempt: { requestId: 'r1', text: 'Moved my stop.', state: null } } })).toBe(true)
  })
  it('ignores an entry kept for a Play that is no longer open', () => {
    expect(hasUnsavedPlayWork({ ...clean, log: { playId: 'another-play', text: 'Moved my stop.', state: '' } })).toBe(false)
  })
})

describe('saved-Play observation cancellation', () => {
  it('passes the same signal to the Play and evidence reads and leaves the originals to load afterwards', async () => {
    const controller = new AbortController()
    const item = { id: 'image', source: 'upload', note: '', markup: null, name: 'image.png',
      contentType: 'image/png', byteLength: 3, createdAtUtc: '2026-10-14T12:00:00Z' }
    const methods = { play: vi.fn().mockResolvedValue(savedPlayFixture), evidence: vi.fn().mockResolvedValue([item]),
      evidenceImage: vi.fn().mockResolvedValue(new Blob(['png'], { type: 'image/png' })) }
    await loadSavedPlay(methods as unknown as WorkspaceApi, savedPlayFixture.summary.id, controller.signal)
    expect(methods.play).toHaveBeenCalledWith(savedPlayFixture.summary.id, controller.signal)
    expect(methods.evidence).toHaveBeenCalledWith(savedPlayFixture.summary.id, controller.signal)
    expect(methods.evidenceImage).not.toHaveBeenCalled()
  })

  it('does not start image reads when an aborted API ignores cancellation and returns late', async () => {
    const controller = new AbortController()
    let finish!: (value: typeof savedPlayFixture) => void
    const methods = { play: vi.fn(() => new Promise(resolve => { finish = resolve })),
      evidence: vi.fn().mockResolvedValue([{ id: 'image' }]), evidenceImage: vi.fn() }
    const result = loadSavedPlay(methods as unknown as WorkspaceApi, savedPlayFixture.summary.id, controller.signal)
    const rejected = expect(result).rejects.toMatchObject({ name: 'AbortError' })
    controller.abort()
    finish(savedPlayFixture)
    await rejected
    expect(methods.evidenceImage).not.toHaveBeenCalled()
  })
})

describe('saved-Play evidence without every original (D-10)', () => {
  const stored = (n: number, note = ''): SavedEvidence => ({
    id: `00000000-0000-4000-8000-00000000000${n}`, playId: savedPlayFixture.summary.id, source: 'upload', contentType: 'image/png',
    sizeBytes: 2048 * n, sha256: 'a'.repeat(64), note, createdAtUtc: '2026-10-14T12:00:00Z', updatedAtUtc: '2026-10-14T12:00:00Z', markup: null,
  })

  it('opens from the Play and evidence metadata when one original returns 503', async () => {
    const items = [stored(1, 'Loads'), stored(2, 'Storage is down')]
    const methods = { play: vi.fn().mockResolvedValue(savedPlayFixture), evidence: vi.fn().mockResolvedValue(items),
      evidenceImage: vi.fn((id: string) => id === items[1]!.id
        ? Promise.reject(new ApiError('http', 'The service is unavailable. Check the database and server configuration.', 503))
        : Promise.resolve(new Blob(['png'], { type: 'image/png' }))) }
    const loaded = await loadSavedPlay(methods as unknown as WorkspaceApi, savedPlayFixture.summary.id)
    expect(loaded.draft.title).toBe(savedPlayFixture.summary.title)
    expect(loaded.draft.evidence).toHaveLength(2)
    expect(loaded.draft.evidence[1]).toMatchObject({ serverId: items[1]!.id, note: 'Storage is down', sizeBytes: 4096,
      savedState: { note: 'Storage is down', markup: null } })
    // Neither original is part of the draft yet; each loads on its own afterwards.
    expect(loaded.draft.evidence.map(item => item.image)).toEqual([undefined, undefined])
    expect(loaded.saved.evidenceIds).toEqual(items.map(item => item.id))
    expect(isDirty(loaded.draft, loaded.saved)).toBe(false)
  })

  it('saves the note of an unloaded image through its own request and never deletes or uploads it', async () => {
    const unloaded = { ...draftEvidenceFromSaved(stored(1, 'old')), note: 'new' }
    // Never produced by the editor (local images always carry their Blob); Save must still not upload it.
    const blobless = { id: 'local', source: 'upload' as const, name: 'b.png', context: '', note: '', markup: null, addedAt: '' }
    const methods = { deleteEvidence: vi.fn(), uploadEvidence: vi.fn(), updateEvidenceMarkup: vi.fn(),
      updateEvidenceNote: vi.fn((_id: string, note: string) => Promise.resolve({ ...stored(1), note })) }
    const result = await syncEvidence(methods as unknown as WorkspaceApi, savedPlayFixture.summary.id, [unloaded, blobless], [unloaded.serverId!])
    expect(result.error).toBeNull()
    expect(methods.updateEvidenceNote).toHaveBeenCalledExactlyOnceWith(unloaded.serverId, 'new')
    expect(methods.deleteEvidence).not.toHaveBeenCalled()
    expect(methods.uploadEvidence).not.toHaveBeenCalled()
    expect(methods.updateEvidenceMarkup).not.toHaveBeenCalled()
    expect(result.evidence[0]).toMatchObject({ serverId: unloaded.serverId, savedState: { note: 'new' } })
    expect(result.evidence[0]!.image).toBeUndefined()
  })
})

describe('Default title when saving', () => {
  const day = new Date(2026, 9, 15, 9)

  it('sends the default name for a blank title and the typed one otherwise, leaving the draft untouched', () => {
    const draft = { ...createDraft(), instrument: 'SOL', direction: 'short' as const }
    expect(fieldsToSave(draft, undefined, day).title).toBe('SOL Short · 15 Oct 2026')
    expect(draft.title).toBe('')
    expect(fieldsToSave({ ...draft, title: 'Breakdown' }, 'draft', day).title).toBe('Breakdown')
    // Everything else in the fields is what fieldsFromDraft would send.
    expect(fieldsToSave(draft, undefined, day)).toMatchObject({ instrument: 'SOL', plan: { direction: 'short' } })
  })

  it('keeps the title of a closed Play blank, so saving its review is still accepted', () => {
    const draft = { ...draftFromSaved(savedPlayFixture, []), title: '' }
    expect(fieldsToSave(draft, 'closed', day).title).toBe('')
  })

  it('trims plan numbers before saving, but sends stored text back exactly as it was', () => {
    const entry = { ...createEntry(0), price: ' 100\n', stops: [{ id: 's', unit: 'price' as const, value: '\t95', share: '100 ' }] }
    const draft = { ...createDraft(), size: ' 250 ', budgetOverride: ' 300', entries: [entry] }
    expect(fieldsToSave(draft, 'draft', day).plan).toMatchObject({ size: '250', budgetOverride: '300',
      entries: [{ price: '100', share: '100', stops: [{ value: '95', share: '100' }] }] })
    // An earlier server stored "1\n"; unchanged, it goes back as it was rather than becoming a plan change.
    const stored = { ...savedPlayFixture, plan: { ...savedPlayFixture.plan, size: '1\n' } }
    const loaded = draftFromSaved(stored, [])
    const state = savedState(stored, loaded)
    expect(fieldsToSave(loaded, 'draft', day, state.plan).plan.size).toBe('1\n')
    expect(fieldsToSave({ ...loaded, size: '2\n' }, 'draft', day, state.plan).plan.size).toBe('2')
  })

  it('does not make a blank, saved Play dirty until it is saved', () => {
    const blank = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, title: '' } }
    const draft = draftFromSaved(blank, [])
    expect(isDirty(draft, savedState(blank, draft))).toBe(false)
  })
})
