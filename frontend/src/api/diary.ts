import {
  hasNoControlCharacters, isCount, isDiaryDecimal, isDiaryNonNegativeDecimal, isDiaryPositiveDecimal, isFilledText, isGuid, isNonBlankText,
  isPositiveCount, isRecord, isText, isUtcTimestamp,
} from './guards'
import { isEntryProgress, isPlayReceipt, isSavedPlay, type EntryProgress, type PlayReceipt, type SavedPlay } from './plays'

export interface ManualExecutionInput {
  requestId: string
  expectedVersion: number
  role: 'entry' | 'exit'
  entryId: string | null
  occurredAtUtc: string
  side: 'buy' | 'sell'
  quantity: string
  price: string
  fee: string | null
  feeCurrency: string | null
  source: string
}
export interface ManualExecutionCorrection extends ManualExecutionInput { reason: string }
export interface ManualExecutionRecord extends Omit<ManualExecutionInput, 'requestId' | 'expectedVersion'> {
  id: string
  createdAtUtc: string
  updatedAtUtc: string
}
export interface ManualExecutions {
  play: SavedPlay
  records: ManualExecutionRecord[]
  history: Array<{ id: string; executionId: string; reason: string | null; occurredAtUtc: string; before: ManualExecutionRecord | null; after: ManualExecutionRecord }>
  totals: {
    enteredQuantity: string
    exitedQuantity: string
    openQuantity: string
    grossResultUsd: string | null
    netResultUsd: string | null
    feesComplete: boolean
  }
  /** Known fees by currency as entered, exactly; absent from servers before Phase 1.1a. */
  fees?: Array<{ token: string; amount: string }>
  /** Executions whose cost is unknown, never counted as zero. */
  unknownFees?: number
  notice: string
  /** Each planned entry's ledger fills beside its plan; absent from servers before Phase 1. */
  entries?: EntryProgress[]
  /** Times, averages and estimated R by Review's rules; absent from servers before Phase 1. */
  receipt?: PlayReceipt | null
}

const facts = (v: Record<string, unknown>) => ['entry', 'exit'].includes(v.role as string) &&
  (v.role === 'entry' ? isFilledText(v.entryId, 64) : v.entryId === null) && isUtcTimestamp(v.occurredAtUtc) &&
  ['buy', 'sell'].includes(v.side as string) && isDiaryPositiveDecimal(v.quantity) && isDiaryPositiveDecimal(v.price) &&
  ((v.fee === null && v.feeCurrency === null) || isDiaryNonNegativeDecimal(v.fee) && isText(v.feeCurrency, 32) && /^[A-Za-z0-9_-]+$/.test(v.feeCurrency)) &&
  isNonBlankText(v.source, 100) && v.source === v.source.trim() && hasNoControlCharacters(v.source)
export const isManualExecutionInput = (v: unknown): v is ManualExecutionInput => isRecord(v) && isGuid(v.requestId) &&
  isPositiveCount(v.expectedVersion) && facts(v)
export const isManualExecutionCorrection = (v: unknown): v is ManualExecutionCorrection => isManualExecutionInput(v) &&
  isRecord(v) && isNonBlankText(v.reason, 2000) && hasNoControlCharacters(v.reason)
export const isManualExecutionRecord = (v: unknown): v is ManualExecutionRecord => isRecord(v) && isGuid(v.id) &&
  facts(v) && isUtcTimestamp(v.createdAtUtc) && isUtcTimestamp(v.updatedAtUtc)
export const isManualExecutions = (v: unknown): v is ManualExecutions => isRecord(v) && isSavedPlay(v.play) &&
  Array.isArray(v.records) && v.records.length <= 200 && v.records.every(isManualExecutionRecord) && Array.isArray(v.history) && v.history.length <= 1000 &&
  v.history.every(h => isRecord(h) && isGuid(h.id) && isGuid(h.executionId) && (h.reason === null || isText(h.reason, 2000)) && isUtcTimestamp(h.occurredAtUtc) &&
    (h.before === null || isManualExecutionRecord(h.before)) && isManualExecutionRecord(h.after)) && isRecord(v.totals) &&
  ['enteredQuantity', 'exitedQuantity', 'openQuantity'].every(key => isDiaryNonNegativeDecimal((v.totals as Record<string, unknown>)[key])) &&
  (v.totals.grossResultUsd === null || isDiaryDecimal(v.totals.grossResultUsd)) &&
  (v.totals.netResultUsd === null || isDiaryDecimal(v.totals.netResultUsd)) && typeof v.totals.feesComplete === 'boolean' && isText(v.notice, 4000) &&
  (v.fees === undefined || Array.isArray(v.fees) && v.fees.length <= 200 &&
    v.fees.every(f => isRecord(f) && isText(f.token, 32) && isDiaryDecimal(f.amount))) &&
  (v.unknownFees === undefined || isCount(v.unknownFees)) &&
  (v.entries === undefined || Array.isArray(v.entries) && v.entries.length <= 20 && v.entries.every(isEntryProgress)) &&
  (v.receipt === undefined || v.receipt === null || isPlayReceipt(v.receipt))

/** GUID request identities also work on LAN HTTP where randomUUID is unavailable. */
export function newDiaryRequestId() {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6]! & 0x0f) | 0x40
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
