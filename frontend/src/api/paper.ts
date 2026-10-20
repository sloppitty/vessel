import { isNonEmptyGuid, isNonNegativeDecimal, isNonBlankText, isPlainDecimal, isRecord, isUtcTimestamp } from './guards'

export type TradingMode = 'real' | 'paper'
export const isTradingMode = (value: unknown): value is TradingMode => value === 'real' || value === 'paper'

export interface PaperSettings {
  enabled: boolean
  checkIntervalMinutes: number
}

export interface PaperPlayState {
  playId: string
  status: 'not-armed' | 'waiting' | 'ready' | 'ambiguous' | 'gap' | 'disabled'
  checkedAtUtc: string | null
  nextCheckAtUtc: string | null
  enteredQuantity: string
  exitedQuantity: string
  openQuantity: string
  realizedPnlUsd: string
  issues: string[]
  executions: Array<{
    id: string
    entryId: string
    levelId: string | null
    role: 'entry' | 'stop' | 'target' | 'exit'
    price: string
    quantity: string
    pnlUsd: string
    occurredAtUtc: string
    candleOpenAtUtc: string
    candleCloseAtUtc: string
  }>
}
export type PaperExecution = PaperPlayState['executions'][number]

export const isPaperSettings = (value: unknown): value is PaperSettings => isRecord(value) &&
  typeof value.enabled === 'boolean' && typeof value.checkIntervalMinutes === 'number' &&
  Number.isInteger(value.checkIntervalMinutes) && value.checkIntervalMinutes >= 1 && value.checkIntervalMinutes <= 1440

const positiveDecimal = (value: unknown): value is string => isNonNegativeDecimal(value) && /[1-9]/.test(value)
const nullableTime = (value: unknown) => value === null || isUtcTimestamp(value)
export const isPaperExecution = (value: unknown): value is PaperExecution => isRecord(value) &&
  isNonEmptyGuid(value.id) && isNonBlankText(value.entryId, 128) && (value.levelId === null || isNonBlankText(value.levelId, 128)) &&
  ['entry', 'stop', 'target', 'exit'].includes(value.role as string) && positiveDecimal(value.price) && positiveDecimal(value.quantity) &&
  isPlainDecimal(value.pnlUsd) && isUtcTimestamp(value.occurredAtUtc) && isUtcTimestamp(value.candleOpenAtUtc) &&
  isUtcTimestamp(value.candleCloseAtUtc) && Date.parse(value.candleCloseAtUtc) > Date.parse(value.candleOpenAtUtc)

/** Explicit simulation facts only: required nulls, exact decimal strings and UTC times are never defaulted or coerced. */
export const isPaperPlayState = (value: unknown): value is PaperPlayState => isRecord(value) && isNonEmptyGuid(value.playId) &&
  ['not-armed', 'waiting', 'ready', 'ambiguous', 'gap', 'disabled'].includes(value.status as string) &&
  nullableTime(value.checkedAtUtc) && nullableTime(value.nextCheckAtUtc) &&
  isNonNegativeDecimal(value.enteredQuantity) && isNonNegativeDecimal(value.exitedQuantity) &&
  isNonNegativeDecimal(value.openQuantity) && isPlainDecimal(value.realizedPnlUsd) &&
  Array.isArray(value.issues) && value.issues.every(issue => isNonBlankText(issue, 2000)) &&
  Array.isArray(value.executions) && value.executions.every(isPaperExecution) &&
  new Set(value.executions.map(execution => execution.id.toLowerCase())).size === value.executions.length
