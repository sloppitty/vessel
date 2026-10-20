import type { ChartDrawing } from '@/components/chart/drawings'
import type { PlanEntry, PlanExit, StructuredReview } from '@/api/plays'
import type { ImageMarkup } from '@/api/markup'

/**
 * A stop or target. A percent value is the return on margin at the play's leverage, so the price
 * moves value ÷ leverage percent from the entry. Share is the part of the entry it closes.
 */
export type DraftExit = PlanExit
export type DraftEntry = PlanEntry

export interface PlayDraft {
  title: string
  accountId: string
  instrument: string
  instrumentSource: 'venue' | 'manual'
  direction: 'long' | 'short'
  sizingMode: 'margin' | 'quantity'
  size: string
  leverage: string
  budgetOverride: string | null
  entries: DraftEntry[]
  notes: Record<'thesis' | 'invalidation' | 'strategy' | 'evidence' | 'review', string>
  /** Chart drawings per venue instrument key (`venueId:contractId`), kept when the instrument changes. */
  drawings: Record<string, ChartDrawing[]>
  /** Chart captures and uploaded images. They stay in the browser until the Play is saved. */
  evidence: DraftEvidence[]
  /** Missing on old session/test drafts means unanswered. */
  structuredReview?: StructuredReview
}

export interface DraftEvidence {
  id: string
  /** Server evidence ID once the image is stored with a saved Play. */
  serverId?: string
  /** Note and marks as last saved, to detect edits that still need saving. */
  savedState?: { note: string; markup: ImageMarkup | null }
  source: 'capture' | 'upload'
  /**
   * The original. Captures and uploads always have it. A saved image opens without it: its original loads afterwards,
   * outside the draft (`useEvidenceImages`), so a missing or unavailable original never blocks or changes the Play.
   */
  image?: Blob
  /** Stored size from the saved image's metadata, shown before its original has loaded. */
  sizeBytes?: number
  /** Upload file name, or a generated name for captures; used when downloading. */
  name: string
  /** What a capture shows, e.g. "BTC · Hyperliquid · 1 hour"; empty for uploads. */
  context: string
  note: string
  /** Marks drawn over the image; the original is never changed. */
  markup: ImageMarkup | null
  addedAt: string
}

const entryColors = ['#b9c9e4', '#edd49e', '#a9d6b6', '#d4b9e4', '#f2b3ac']
// Saved plans keep these IDs, so they must not repeat across page loads. randomUUID needs a secure
// context, which HTTP LAN previews lack, so time and randomness stand in for it.
let nextId = 0
const localId = () => `d${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}${(++nextId).toString(36)}`

export const createEvidenceId = localId

export function createExit(share = ''): DraftExit {
  return { id: localId(), unit: 'price', value: '', share }
}

export function createEntry(index: number): DraftEntry {
  return {
    id: localId(), name: `Entry ${index + 1}`, color: entryColors[index % entryColors.length]!,
    share: index === 0 ? '100' : '', price: '',
    stops: [createExit('100')],
    targets: [createExit('100')],
  }
}

/** Renames default-named entries to "Entry 1", "Entry 2"… in order, e.g. after one is removed. Colors stay. */
export function renumberEntries(entries: readonly DraftEntry[]): DraftEntry[] {
  return entries.map((entry, index) => /^Entry \d+$/.test(entry.name) && entry.name !== `Entry ${index + 1}` ? { ...entry, name: `Entry ${index + 1}` } : entry)
}

// Plain share bookkeeping, not a sizing calculation. Two decimals with the remainder on the last entry.
export function equalShares(count: number) {
  const base = Math.floor(10000 / count) / 100
  return Array.from({ length: count }, (_, index) =>
    String(index === count - 1 ? Number((100 - base * (count - 1)).toFixed(2)) : base))
}

/** Gives every entry the same share of the position. */
export function splitEqually(entries: readonly DraftEntry[]): DraftEntry[] {
  const shares = equalShares(entries.length)
  return entries.map((entry, index) => entry.share === shares[index] ? entry : { ...entry, share: shares[index]! })
}

/** Adds an entry and splits the position equally; the owner can change the shares afterwards. */
export function addEntry(entries: readonly DraftEntry[], entry: DraftEntry): DraftEntry[] {
  return splitEqually([...entries, entry])
}

/**
 * Removes an entry and renumbers the rest. A single remaining entry takes the whole position, and
 * an equal split stays equal; other shares are left for the owner to rebalance.
 */
export function removeEntry(entries: readonly DraftEntry[], id: string): DraftEntry[] {
  if (entries.length <= 1 || !entries.some(entry => entry.id === id)) return [...entries]
  const wasEqual = entries.every((entry, index) => entry.share === equalShares(entries.length)[index])
  const remaining = renumberEntries(entries.filter(entry => entry.id !== id))
  return remaining.length === 1 || wasEqual ? splitEqually(remaining) : remaining
}

/** A blank entry after the existing ones, named after the first free "Entry n". */
export function createNextEntry(entries: readonly DraftEntry[]): DraftEntry {
  let index = entries.length
  while (entries.some(entry => entry.name === `Entry ${index + 1}`)) index += 1
  return createEntry(index)
}

export function createDraft(): PlayDraft {
  return {
    title: '', accountId: '', instrument: '', instrumentSource: 'manual', direction: 'long',
    sizingMode: 'margin', size: '', leverage: '1', budgetOverride: null,
    entries: [createEntry(0)],
    notes: { thesis: '', invalidation: '', strategy: '', evidence: '', review: '' },
    drawings: {},
    evidence: [],
  }
}
