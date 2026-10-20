import { describe, expect, it } from 'vitest'
import { isPaperExecution, isPaperPlayState, isPaperSettings, isTradingMode, type PaperPlayState } from './paper'

const execution: PaperPlayState['executions'][number] = {
  id: '10000000-0000-4000-8000-000000000001',
  entryId: 'dm9x4k12entry',
  levelId: null,
  role: 'entry',
  price: '99999.12345678901234567890123',
  quantity: '0.0000000000000000000000000001',
  pnlUsd: '0',
  occurredAtUtc: '2026-10-20T12:01:00Z',
  candleOpenAtUtc: '2026-10-20T12:00:00Z',
  candleCloseAtUtc: '2026-10-20T12:01:00Z',
}
const state: PaperPlayState = {
  playId: '30000000-0000-4000-8000-000000000001',
  status: 'waiting',
  checkedAtUtc: null,
  nextCheckAtUtc: '2026-10-20T13:00:00.1234567+00:00',
  enteredQuantity: '0.0000000000000000000000000001',
  exitedQuantity: '0',
  openQuantity: '0.0000000000000000000000000001',
  realizedPnlUsd: '-0.0000000000000000000000000001',
  issues: [],
  executions: [execution],
}

describe('paper API contract', () => {
  it('accepts only explicit trading modes, without silently defaulting an invalid value', () => {
    expect(isTradingMode('real')).toBe(true)
    expect(isTradingMode('paper')).toBe(true)
    for (const value of [undefined, null, false, 'Paper', 'virtual', '']) expect(isTradingMode(value)).toBe(false)
  })

  it.each([1, 60, 1440])('accepts independent paper settings at %s minutes', checkIntervalMinutes => {
    expect(isPaperSettings({ enabled: false, checkIntervalMinutes })).toBe(true)
    expect(isPaperSettings({ enabled: true, checkIntervalMinutes })).toBe(true)
  })

  it('requires a boolean switch and a bounded whole-number interval, without coercion or defaults', () => {
    for (const value of [null, [], {}, { enabled: false }, { checkIntervalMinutes: 60 },
      { enabled: 'false', checkIntervalMinutes: 60 }, { enabled: 0, checkIntervalMinutes: 60 }])
      expect(isPaperSettings(value)).toBe(false)
    for (const checkIntervalMinutes of [null, undefined, '60', 0, -1, 1.5, 1441, Infinity, NaN])
      expect(isPaperSettings({ enabled: false, checkIntervalMinutes })).toBe(false)
  })

  it('preserves exact strings and accepts all pinned states and roles', () => {
    for (const status of ['not-armed', 'waiting', 'ready', 'ambiguous', 'gap', 'disabled'])
      expect(isPaperPlayState({ ...state, status })).toBe(true)
    for (const role of ['entry', 'stop', 'target', 'exit'])
      expect(isPaperPlayState({ ...state, executions: [{ ...execution, role, levelId: execution.entryId }] })).toBe(true)
    expect(isPaperPlayState({ ...state, executions: [] })).toBe(true)
    expect(isPaperPlayState(state)).toBe(true)
    expect(state.executions[0]?.price).toBe(execution.price)
  })

  it('accepts bounded local entry/level IDs, not just GUIDs', () => {
    expect(isPaperExecution(execution)).toBe(true)
    expect(isPaperExecution({ ...execution, entryId: 'd-entry-1', levelId: 'd-stop-1' })).toBe(true)
    expect(isPaperExecution({ ...execution, entryId: 'd'.repeat(128), levelId: 'd'.repeat(128) })).toBe(true)
    for (const field of ['entryId', 'levelId'])
      for (const value of ['', ' ', 42, 'd'.repeat(129)])
        expect(isPaperExecution({ ...execution, [field]: value })).toBe(false)
  })

  it('requires every pinned state field, including explicit null timestamps', () => {
    for (const key of Object.keys(state)) {
      const missing: Record<string, unknown> = { ...state }
      delete missing[key]
      expect(isPaperPlayState(missing), key).toBe(false)
    }
    for (const status of ['open', 'filled', 'unknown', null]) expect(isPaperPlayState({ ...state, status })).toBe(false)
    expect(isPaperPlayState(null)).toBe(false)
    expect(isPaperPlayState([])).toBe(false)
    expect(isPaperPlayState({ ...state, playId: 'not-a-guid' })).toBe(false)
    expect(isPaperPlayState({ ...state, playId: '00000000-0000-0000-0000-000000000000' })).toBe(false)
  })

  it('rejects numeric, malformed and negative quantity values; signed exact P&L remains valid', () => {
    for (const field of ['enteredQuantity', 'exitedQuantity', 'openQuantity', 'realizedPnlUsd']) {
      for (const value of [1, null, undefined, '', 'NaN', 'Infinity', '1e3', '+2', '.5', ' 1'])
        expect(isPaperPlayState({ ...state, [field]: value }), `${field}: ${value}`).toBe(false)
    }
    for (const field of ['enteredQuantity', 'exitedQuantity', 'openQuantity'])
      expect(isPaperPlayState({ ...state, [field]: '-1' })).toBe(false)
    expect(isPaperPlayState({ ...state, realizedPnlUsd: '-12.3400' })).toBe(true)
  })

  it('rejects missing or malformed explicit simulation rows, duplicate IDs and invalid candle bounds', () => {
    for (const key of Object.keys(execution)) {
      const missing: Record<string, unknown> = { ...execution }
      delete missing[key]
      expect(isPaperPlayState({ ...state, executions: [missing] }), key).toBe(false)
    }
    for (const row of [null, {}, { ...execution, role: 'fill' }, { ...execution, id: '1' }, { ...execution, levelId: '' },
      { ...execution, price: 10 }, { ...execution, price: '0' }, { ...execution, quantity: '0' },
      { ...execution, quantity: '-1' }, { ...execution, pnlUsd: NaN },
      { ...execution, candleCloseAtUtc: execution.candleOpenAtUtc }])
      expect(isPaperPlayState({ ...state, executions: [row] })).toBe(false)
    expect(isPaperPlayState({ ...state, executions: [execution, execution] })).toBe(false)
    expect(isPaperPlayState({ ...state, executions: null })).toBe(false)
  })

  it('rejects invalid UTC timestamps and unbounded or non-text issues', () => {
    for (const field of ['checkedAtUtc', 'nextCheckAtUtc'])
      for (const value of ['', 'today', '2026-02-31T12:00:00Z', '2026-10-20T12:00:00+03:00', 42])
        expect(isPaperPlayState({ ...state, [field]: value })).toBe(false)
    for (const field of ['occurredAtUtc', 'candleOpenAtUtc', 'candleCloseAtUtc'])
      for (const value of [null, '2026-02-31T12:00:00Z', '2026-10-20T12:00:00'])
        expect(isPaperPlayState({ ...state, executions: [{ ...execution, [field]: value }] })).toBe(false)
    for (const issues of [null, [''], ['   '], [42], ['x'.repeat(2001)]])
      expect(isPaperPlayState({ ...state, issues })).toBe(false)
    expect(isPaperPlayState({ ...state, issues: ['Same-candle entry and stop; order is unknown.'] })).toBe(true)
  })
})
