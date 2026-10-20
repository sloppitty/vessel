import type { PlayNoteState } from '@/api/plays'

/** The log entry being written: kept by the page for its Play, so switching journal tabs never loses it. */
export interface PlayLogDraft {
  playId: string
  text: string
  state: PlayNoteState | ''
  /** The request last sent for this entry; a retry of the same entry sends it again, so it is never written twice. */
  attempt?: { requestId: string; text: string; state: PlayNoteState | null }
}

/** Whether a log entry is being written or its write is still unconfirmed: unsaved work, like a manual execution draft. */
export const hasLogDraft = (draft: PlayLogDraft | undefined) => !!draft && (draft.text.trim() !== '' || !!draft.attempt)

/** A change to the kept log draft, applied to its current value: a late write never acts on an outdated copy. */
export type PlayLogChange = (current: PlayLogDraft | undefined) => PlayLogDraft | undefined
