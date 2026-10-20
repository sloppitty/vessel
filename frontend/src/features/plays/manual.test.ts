import { afterEach, describe, expect, it } from 'vitest'
import { emptyManualDraft, manualBatchRequest, manualCorrectionDraft, manualRequest, sideFor } from './manual'
import { nowText, parseZoneText, zoneText } from './manual-time'

const draft = { ...emptyManualDraft(), entryId: 'entry-1', quantity: '1', price: '100', source: 'Statement' }
const now = Date.parse('2026-10-20T12:00:00Z')
const originalZone = process.env.TZ
afterEach(() => { process.env.TZ = originalZone })

describe('manual execution time in the display zone (phase 1, 1.6b)', () => {
  it.each([
    ['2026-10-13T10:00', '2026-10-13T10:00:00Z'],
    ['2026-10-13 10:00:00', '2026-10-13T10:00:00Z'],
    ['2026-10-13T10:00:00.123456', '2026-10-13T10:00:00.123456Z'],
    ['2026-10-13T10:00:00.123456Z', '2026-10-13T10:00:00.123456Z'],
    ['2026-10-13T10:00:00.123456+00:00', '2026-10-13T10:00:00.123456Z'],
    ['2026-10-13T11:00:00+01:00', '2026-10-13T10:00:00Z'],
  ])('reads %s in UTC without losing precision', (time, expected) => {
    expect(manualRequest({ ...draft, occurredAt: time }, 2, 'long', ['entry-1'], 'utc', now)).toHaveProperty('input.occurredAtUtc', expected)
  })

  it('reads local input in the browser zone and sends UTC, keeping an explicit zone as written', () => {
    process.env.TZ = 'Europe/Madrid'
    // Summer time: UTC+2.
    expect(manualRequest({ ...draft, occurredAt: '2026-10-13 10:00:00.5' }, 2, 'long', ['entry-1'], 'local', now))
      .toHaveProperty('input.occurredAtUtc', '2026-10-13T08:00:00.5Z')
    expect(manualRequest({ ...draft, occurredAt: '2026-10-13 10:00:00Z' }, 2, 'long', ['entry-1'], 'local', now))
      .toHaveProperty('input.occurredAtUtc', '2026-10-13T10:00:00Z')
    // A stored time shows in the zone and goes back to the same instant, digit for digit.
    expect(zoneText('2026-10-13T08:00:00.123456Z', 'local')).toBe('2026-10-13 10:00:00.123456')
    expect(parseZoneText(zoneText('2026-10-13T08:00:00.123456Z', 'local'), 'local')).toEqual({ utc: '2026-10-13T08:00:00.123456Z', fraction: '.123456' })
    expect(zoneText('2026-10-13T08:00:00.123456+00:00', 'utc')).toBe('2026-10-13 08:00:00.123456')
    // The spring-forward gap does not exist; it is refused rather than moved.
    expect(parseZoneText('2026-03-29 02:30:00', 'local')).toEqual({ problem: 'missing' })
    expect(nowText('local', now)).toBe('2026-10-20 14:00:00')
    expect(nowText('utc', now + 999)).toBe('2026-10-20 12:00:00')
  })

  it.each([
    ['', 'Enter when it executed.'],
    ['not a time', 'Enter a date and time such as 2026-10-13 10:00:00.'],
    ['2026-02-30T10:00:00Z', 'That time does not exist in UTC.'],
    ['2026-10-21T10:00:00Z', 'An execution cannot be in the future.'],
    ['2026-10-13T10:00:00.1234567', 'Use whole microseconds or coarser: at most six decimal places.'],
  ])('puts the error for %j on the time field', (time, message) => {
    const result = manualRequest({ ...draft, occurredAt: time }, 2, 'long', ['entry-1'], 'utc', now)
    expect(result).toEqual({ errors: { occurredAt: message } })
  })
})

describe('manual execution fields (phase 1, 1.6b)', () => {
  const time = { occurredAt: '2026-10-13 10:00:00' }
  it('puts each error on its own field, an empty Source included', () => {
    const result = manualRequest({ ...emptyManualDraft(), ...time, quantity: '0', price: 'x', fee: '1', source: '  ' }, 2, 'long', ['entry-1'], 'utc', now)
    expect(result).toEqual({ errors: {
      entryId: 'Choose an entry from the saved plan.',
      quantity: 'Enter a quantity above zero, such as 0.5.',
      price: 'Enter a price above zero, such as 100.',
      feeCurrency: 'Name the fee currency, or leave both fee fields blank for unknown.',
      source: 'Name where this execution comes from, such as a broker statement.',
    } })
  })

  it('takes the side from the role and the direction unless the owner chose one', () => {
    expect([sideFor('entry', 'long'), sideFor('exit', 'long'), sideFor('entry', 'short'), sideFor('exit', 'short')]).toEqual(['buy', 'sell', 'sell', 'buy'])
    expect(manualRequest({ ...draft, ...time }, 2, 'short', ['entry-1'], 'utc', now)).toHaveProperty('input.side', 'sell')
    expect(manualRequest({ ...draft, ...time, role: 'exit' }, 2, 'long', ['entry-1'], 'utc', now)).toHaveProperty('input.side', 'sell')
    expect(manualRequest({ ...draft, ...time, side: 'sell' }, 2, 'long', ['entry-1'], 'utc', now))
      .toEqual({ errors: { side: 'This long play needs buy for an entry.' } })
  })

  it('sends trimmed text and a reason for a correction', () => {
    const record = { id: '00000000-0000-4000-8000-000000000001', role: 'entry' as const, entryId: 'entry-1', occurredAtUtc: '2026-10-13T10:00:00.25Z',
      side: 'buy' as const, quantity: '1', price: '100', fee: null, feeCurrency: null, source: 'Statement', createdAtUtc: '2026-10-13T10:00:00Z',
      updatedAtUtc: '2026-10-13T10:00:00Z' }
    const correction = manualCorrectionDraft(record, 'utc')
    expect(correction.occurredAt).toBe('2026-10-13 10:00:00.25')
    expect(manualRequest({ ...correction, quantity: ' 2 ' }, 2, 'long', ['entry-1'], 'utc', now))
      .toEqual({ errors: { reason: 'Give a reason for the correction.' } })
    expect(manualRequest({ ...correction, quantity: ' 2 ', reason: ' Statement says two. ' }, 2, 'long', ['entry-1'], 'utc', now))
      .toMatchObject({ input: { quantity: '2', reason: 'Statement says two.', occurredAtUtc: '2026-10-13T10:00:00.25Z' } })
    // A batch names the row each error belongs to.
    const batch = manualBatchRequest({ ...correction, reason: 'Two.', together: [{ ...correction, executionId: 'other', source: '' }] }, 2, 'utc', now)
    expect(batch).toEqual({ errors: {}, rows: [{ source: 'Name where this execution comes from, such as a broker statement.' }] })
  })
})
