import { describe, expect, it, vi } from 'vitest'
import { createDraft, createEntry } from './draft'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { createPlaysSession, draftFromSaved, fieldsToSave, hasDraftContent, isDirty, loadSavedPlay, observedPlayVersion, savedState, startNewPlay } from './saved'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'

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


describe('saved-Play observation cancellation', () => {
  it('passes the same signal to the Play, evidence and image reads', async () => {
    const controller = new AbortController()
    const item = { id: 'image', source: 'upload', note: '', markup: null, name: 'image.png',
      contentType: 'image/png', byteLength: 3, createdAtUtc: '2026-10-14T12:00:00Z' }
    const methods = { play: vi.fn().mockResolvedValue(savedPlayFixture), evidence: vi.fn().mockResolvedValue([item]),
      evidenceImage: vi.fn().mockResolvedValue(new Blob(['png'], { type: 'image/png' })) }
    await loadSavedPlay(methods as unknown as WorkspaceApi, savedPlayFixture.summary.id, controller.signal)
    expect(methods.play).toHaveBeenCalledWith(savedPlayFixture.summary.id, controller.signal)
    expect(methods.evidence).toHaveBeenCalledWith(savedPlayFixture.summary.id, controller.signal)
    expect(methods.evidenceImage).toHaveBeenCalledWith('image', controller.signal)
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
