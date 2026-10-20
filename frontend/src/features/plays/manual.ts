import { isManualCorrectionBatch, isManualExecutionCorrection, isManualExecutionInput, newDiaryRequestId, type ManualCorrectionBatch, type ManualExecutionCorrection, type ManualExecutionInput, type ManualExecutionRecord } from '@/api/diary'
import { hasNoControlCharacters, isDiaryNonNegativeDecimal, isDiaryPositiveDecimal } from '@/api/guards'
import { parseZoneText, zoneName, zoneText, type EntryZone } from './manual-time'

/** Kept with the shell's Plays session, including the identity of an uncertain write. */
export interface ManualExecutionDraft {
  executionId: string | null
  role: 'entry' | 'exit'
  entryId: string
  /** As the owner typed it, in the display time zone (phase 1, 1.6b); sent as UTC. */
  occurredAt: string
  /** Blank follows the role and the Play's direction; the owner may still choose (phase 1, 1.6b). */
  side: 'buy' | 'sell' | ''
  quantity: string
  price: string
  fee: string
  feeCurrency: string
  source: string
  reason: string
  /** Other existing executions corrected with this one, under its reason, all or nothing (phase 1, 1.3b). */
  together?: ManualExecutionDraft[]
  attempt?: ManualExecutionInput | ManualExecutionCorrection | ManualCorrectionBatch
}
export const emptyManualDraft = (): ManualExecutionDraft => ({
  executionId: null, role: 'entry', entryId: '', occurredAt: '', side: '', quantity: '', price: '',
  fee: '', feeCurrency: '', source: '', reason: '',
})
export const hasManualDraft = (draft?: ManualExecutionDraft) => !!draft &&
  !!(draft.executionId || draft.entryId || draft.occurredAt || draft.side || draft.quantity || draft.price || draft.fee || draft.feeCurrency || draft.source || draft.reason || draft.attempt ||
    draft.together?.length)
export function manualCorrectionDraft(record: ManualExecutionRecord, zone: EntryZone = 'local'): ManualExecutionDraft {
  return { ...emptyManualDraft(), executionId: record.id, role: record.role, entryId: record.entryId ?? '',
    occurredAt: zoneText(record.occurredAtUtc, zone), side: record.side, quantity: record.quantity, price: record.price,
    fee: record.fee ?? '', feeCurrency: record.feeCurrency ?? '', source: record.source }
}

/** The side an execution of this role takes in a Play of this direction: entries open it, exits close it. */
export const sideFor = (role: ManualExecutionDraft['role'], direction: 'long' | 'short') =>
  (role === 'entry') === (direction === 'long') ? 'buy' as const : 'sell' as const

/** The fields of a manual execution form that can each carry their own error (phase 1, 1.6b). */
export type ManualField = 'entryId' | 'occurredAt' | 'side' | 'quantity' | 'price' | 'fee' | 'feeCurrency' | 'source' | 'reason'
export type ManualErrors = Partial<Record<ManualField, string>>

type Facts = { occurredAtUtc: string; quantity: string; price: string; fee: string | null; feeCurrency: string | null; source: string }

/** One execution's facts as the server reads them, or the errors of each field that is wrong. */
function facts(row: ManualExecutionDraft, zone: EntryZone, now: number): { facts: Facts } | { errors: ManualErrors } {
  const errors: ManualErrors = {}
  let occurredAtUtc = ''
  const parsed = parseZoneText(row.occurredAt, zone)
  if (!row.occurredAt.trim()) errors.occurredAt = 'Enter when it executed.'
  else if ('problem' in parsed) errors.occurredAt = parsed.problem === 'missing' ? `That time does not exist in ${zoneName(zone)}.`
    : 'Enter a date and time such as 2026-10-13 10:00:00.'
  else if (parsed.fraction.length > 7) errors.occurredAt = 'Use whole microseconds or coarser: at most six decimal places.'
  else if (Date.parse(parsed.utc) > now) errors.occurredAt = 'An execution cannot be in the future.'
  else occurredAtUtc = parsed.utc
  const [quantity, price, fee, feeCurrency, source] = [row.quantity, row.price, row.fee, row.feeCurrency, row.source].map(value => value.trim()) as
    [string, string, string, string, string]
  if (!isDiaryPositiveDecimal(quantity)) errors.quantity = 'Enter a quantity above zero, such as 0.5.'
  if (!isDiaryPositiveDecimal(price)) errors.price = 'Enter a price above zero, such as 100.'
  if (fee || feeCurrency) {
    if (!fee) errors.fee = 'Enter the fee, 0 for none, or leave both fee fields blank for unknown.'
    else if (!isDiaryNonNegativeDecimal(fee)) errors.fee = 'Enter a fee of zero or more.'
    if (!feeCurrency) errors.feeCurrency = 'Name the fee currency, or leave both fee fields blank for unknown.'
    else if (!/^[A-Za-z0-9_-]{1,32}$/.test(feeCurrency)) errors.feeCurrency = 'Use letters, digits, - or _, at most 32.'
  }
  if (!source) errors.source = 'Name where this execution comes from, such as a broker statement.'
  else if (!hasNoControlCharacters(source)) errors.source = 'Remove line breaks and other control characters.'
  if (Object.keys(errors).length) return { errors }
  return { facts: { occurredAtUtc, quantity, price, fee: fee || null, feeCurrency: feeCurrency || null, source } }
}

const reasonError = (reason: string) => !reason.trim() ? 'Give a reason for the correction.'
  : !hasNoControlCharacters(reason) ? 'Remove control characters from the reason.' : undefined

/**
 * Several corrections with one reason, sent as one batch (phase 1, 1.3b). Each row keeps its role, entry and side; the
 * server checks only the final ledger as a whole. A frozen attempt is resent unchanged. Errors come per field, and per
 * row for the executions corrected together.
 */
export function manualBatchRequest(draft: ManualExecutionDraft, version: number, zone: EntryZone = 'local', now = Date.now()) {
  if (draft.attempt && 'corrections' in draft.attempt) return isManualCorrectionBatch(draft.attempt) ? { batch: draft.attempt } as const
    : { errors: {}, rows: [] } as const
  const rows = [draft, ...draft.together ?? []].map(row => facts(row, zone, now))
  const reason = reasonError(draft.reason)
  if (reason || rows.some(row => 'errors' in row)) {
    const [first, ...others] = rows.map(row => 'errors' in row ? row.errors : {})
    return { errors: { ...first, ...(reason ? { reason } : {}) }, rows: others } as const
  }
  const batch: ManualCorrectionBatch = {
    requestId: newDiaryRequestId(), expectedVersion: version, reason: draft.reason.trim(),
    corrections: [draft, ...draft.together ?? []].map((row, index) => ({
      executionId: row.executionId ?? '', role: row.role, entryId: row.role === 'entry' ? row.entryId : null,
      side: row.side as 'buy' | 'sell', ...(rows[index] as { facts: Facts }).facts,
    })),
  }
  return isManualCorrectionBatch(batch) ? { batch } as const : { errors: {}, rows: [] } as const
}

export function manualRequest(draft: ManualExecutionDraft, version: number, direction: 'long' | 'short', entryIds: string[],
  zone: EntryZone = 'local', now = Date.now()) {
  if (draft.attempt && !('corrections' in draft.attempt)) return isManualExecutionInput(draft.attempt) ? { input: draft.attempt } as const
    : { errors: {} } as const
  const expected = sideFor(draft.role, direction)
  const side = draft.side || expected
  const row = facts(draft, zone, now)
  const errors: ManualErrors = 'errors' in row ? { ...row.errors } : {}
  if (draft.role === 'entry' && !entryIds.includes(draft.entryId)) errors.entryId = 'Choose an entry from the saved plan.'
  if (side !== expected) errors.side = `This ${direction} play needs ${expected} for an ${draft.role}.`
  const reason = draft.executionId ? reasonError(draft.reason) : undefined
  if (reason) errors.reason = reason
  if (Object.keys(errors).length || 'errors' in row) return { errors } as const
  const input: ManualExecutionInput | ManualExecutionCorrection = {
    requestId: newDiaryRequestId(), expectedVersion: version, role: draft.role, entryId: draft.role === 'entry' ? draft.entryId : null,
    side, ...row.facts, ...(draft.executionId ? { reason: draft.reason.trim() } : {}),
  }
  if (!isManualExecutionInput(input) || draft.executionId && !isManualExecutionCorrection(input)) return { errors: {} } as const
  return { input } as const
}
