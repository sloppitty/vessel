import { describe, expect, it } from 'vitest'
import { createDraft, createEntry, createExit } from './draft'
import { levelPrice, leverageOf } from './levels'
import { planIssues } from './planChecks'
import { positionRiskSummary } from './position-risk-summary'
import { positionSize } from './sizing'
import { lossPerUnit } from './suggestions'
import {
  finitePlanNumber, formatPlanNumber, isPlanText, keptPlanTextTitle, numberInputShows, plainDecimalRule, planInputType, planNumber, planShare,
  planTextState, positiveFinitePlanNumber, positivePlanNumber, serverPlanNumber,
} from './numbers'
import shared from '@/test/contracts/plan-numbers.json?raw'

/** The list the server's PlayDocumentTests reads too; both runtimes must agree on it. */
const fixture = JSON.parse(shared) as { accepted: string[]; rejected: string[]; negativePercent: string[] }
const show = (text: string) => JSON.stringify(text)
const profiles = [planNumber, finitePlanNumber, positivePlanNumber, positiveFinitePlanNumber, planShare]

describe('draft plan number profiles', () => {
  it.each(['', ' ', '.5', '1.', '+1', '-1', '-0', '1e2', '0x10', 'Infinity', 'NaN', '1 2'])('rejects %j in every plain-decimal profile', value => {
    for (const parse of profiles) {
      expect(parse(value)).toBeNull()
    }
  })
  it.each(['1', '01.25', ' 1.25 ', '\t1.25\n'])('retains surrounding whitespace and leading zero acceptance for %j', value => {
    for (const parse of profiles) {
      expect(parse(value)).toBe(Number(value))
    }
  })
  it('keeps zero and share boundaries, and reads nothing the server would not store', () => {
    for (const zero of ['0', '00.000']) {
      expect(planNumber(zero)).toBe(0)
      expect(finitePlanNumber(zero)).toBe(0)
      expect(planShare(zero)).toBe(0)
      expect(positivePlanNumber(zero)).toBeNull()
      expect(positiveFinitePlanNumber(zero)).toBeNull()
    }
    expect(planShare('100')).toBe(100)
    expect(planShare('100.01')).toBeNull()
    // Longer than 64 characters, or more precise than a C# decimal: not plan text, so no profile reads a number.
    for (const text of [`0.${'0'.repeat(324)}1`, '9'.repeat(309), '9'.repeat(65), '0.00000000000000000000000000001', '79228162514264337593543950336']) {
      for (const parse of profiles) expect(parse(text), show(text)).toBeNull()
    }
  })
  it('uses the matching profiles in levels, sizing, suggestions, checks and full-position estimates', () => {
    const entry = { ...createEntry(0), price: ' 100 ', stops: [{ ...createExit('100'), value: ' 95 ' }] }
    const draft = { ...createDraft(), size: ' 2 ', sizingMode: 'quantity' as const, entries: [entry] }
    expect(levelPrice(100, entry.stops[0]!, 'stop', 'long')).toBe(95)
    expect(positionSize(draft, 1).quantity).toBe(2)
    expect(lossPerUnit(draft.entries, 'long', 1)).toBe(5)
    expect(planIssues(draft.entries, 'long')).toEqual([])
    expect(positionRiskSummary(draft).stops.totalAmount).toBe(10)

    // Text the server would refuse is unknown everywhere, not Infinity in one place and an issue in another.
    const overflow = '9'.repeat(309)
    expect(positionSize({ ...draft, size: overflow }, 1).quantity).toBeNull()
    expect(positionRiskSummary({ ...draft, size: overflow }).quantity).toBeNull()
    expect(levelPrice(100, { ...entry.stops[0]!, value: overflow }, 'stop', 'long')).toBeNull()
    expect(planIssues([{ ...entry, price: overflow }], 'short')).toEqual([])
    expect(lossPerUnit([{ ...entry, price: overflow }], 'long', 1)).toBeNull()
  })
  it('reads leverage as the server does: whole digits, otherwise 1', () => {
    expect(leverageOf('1e2')).toBe(1)
    expect(leverageOf(' 2 ')).toBe(2)
    expect(leverageOf('2.5')).toBe(1)
    expect(leverageOf('0')).toBe(1)
    expect(positionRiskSummary({ ...createDraft(), leverage: '1e2', size: '1', sizingMode: 'quantity' }).quantity).toBeNull()
  })
})

describe('plan number text shared with the server (test/contracts/plan-numbers.json)', () => {
  it('accepts the listed text on write and reads it as that number', () => {
    for (const text of fixture.accepted) {
      expect(isPlanText(text), show(text)).toBe(true)
      expect(planNumber(text), show(text)).toBe(text === '' ? null : Number(text))
    }
  })
  it('refuses the rest on write, and reads only what trimming before the save turns into plan text', () => {
    for (const text of fixture.rejected) {
      expect(isPlanText(text), show(text)).toBe(false)
      const trimmed = text.trim()
      expect(planNumber(text), show(text)).toBe(trimmed !== '' && isPlanText(trimmed) ? Number(trimmed) : null)
    }
  })
  it('gives each refusal the server\'s reason: the plain-decimal rule, or the sign reason for a negative plain decimal in %', () => {
    for (const text of fixture.rejected) {
      // The browser trims before saving, so text that trims to plan text is valid here and saved trimmed.
      if (isPlanText(text.trim())) continue
      const negative = fixture.negativePercent.includes(text)
      expect(planTextState(text, 'Entry 1 price'), show(text)).toEqual({ status: 'invalid', reason: `Entry 1 price: ${plainDecimalRule}` })
      expect(planTextState(text, 'Entry 1 stop 1', null, 'stop'), show(text)).toEqual({ status: 'invalid', reason: negative
        ? 'Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative.' : `Entry 1 stop 1: ${plainDecimalRule}` })
      expect(planTextState(text, 'Entry 1 target 1', null, 'target'), show(text)).toEqual({ status: 'invalid', reason: negative
        ? 'Entry 1 target 1: a percentage cannot be negative; use a price instead.' : `Entry 1 target 1: ${plainDecimalRule}` })
    }
    for (const text of fixture.negativePercent) expect(fixture.rejected, show(text)).toContain(text)
  })
  // Review probe verify-opus-dotnet/More4.cs Grammar: the server saved these (1e2 as 100, .5 as 0.5) while the browser read them as missing.
  it.each(['1e2', '100.', '.5', '+100', '-1'])('treats probe text %j as missing and refuses to save it, as the server now does', text => {
    expect(planNumber(text)).toBeNull()
    expect(planTextState(text, 'Entry 1 price')).toEqual({ status: 'invalid', reason: `Entry 1 price: ${plainDecimalRule}` })
  })
  it('keeps unchanged stored text saveable, refuses negative percentages and keeps 0% as break-even', () => {
    expect(planTextState('1e2', 'Entry 1 price', '1e2')).toEqual({ status: 'kept' })
    expect(planTextState('1E2', 'Entry 1 price', '1e2').status).toBe('invalid')
    expect(planTextState(' 95 ', 'Entry 1 stop 1')).toEqual({ status: 'valid' })
    expect(planTextState('-5', 'Entry 1 stop 1', null, 'stop')).toEqual({ status: 'invalid',
      reason: 'Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative.' })
    expect(planTextState('-10', 'Entry 1 target 1', '10', 'target')).toEqual({ status: 'invalid',
      reason: 'Entry 1 target 1: a percentage cannot be negative; use a price instead.' })
    expect(planTextState('-5', 'Entry 1 stop 1', '-5', 'stop')).toEqual({ status: 'kept' })
    expect(planTextState('0', 'Entry 1 stop 1', null, 'stop')).toEqual({ status: 'valid' })
    // The grammar decides first, as on the server: an exponent or zero after the sign is not a negative percentage.
    for (const text of ['-1e30', '-1e-30', '-1e1', '-0']) {
      expect(planTextState(text, 'Entry 1 stop 1', null, 'stop'), text).toEqual({ status: 'invalid', reason: `Entry 1 stop 1: ${plainDecimalRule}` })
    }
  })
  it('flags stored text that only trims to plan text, and never trailing space around a stored plain value', () => {
    // An earlier server stored "1\n"; it goes back unchanged, so it is kept (a warning), not silently valid.
    expect(planTextState('1\n', 'Size', '1\n')).toEqual({ status: 'kept' })
    expect(keptPlanTextTitle('1\n')).toBe('Saved earlier as “1\\n”, which is not a plain decimal. It saves unchanged; enter a plain decimal to replace it.')
    expect(keptPlanTextTitle('+100')).toMatch(/^Saved earlier as “\+100”, .* estimates here ignore it/)
    expect(planTextState('1\n', 'Size')).toEqual({ status: 'valid' })
    expect(planTextState(' 1', 'Size', '1')).toEqual({ status: 'valid' })
  })
  it('renders text a number input would blank as a text field', () => {
    for (const shown of ['', '100', '0.5', '.5', '1e2', '-5', '007']) {
      expect(numberInputShows(shown), show(shown)).toBe(true)
      expect(planInputType(shown)).toEqual({ type: 'number' })
    }
    for (const blanked of ['+100', '100.', '1.e1', '1\n', ' 1', '1e999', '1,5']) {
      expect(numberInputShows(blanked), show(blanked)).toBe(false)
      expect(planInputType(blanked)).toEqual({ type: 'text', inputMode: 'decimal' })
    }
  })
  it('reads stored text as the server\'s side check does, without changing what estimates read', () => {
    const reads: [string, number | null][] = [['1e2', 100], ['+100', 100], ['100.', 100], ['.5', 0.5], [' 95 ', 95], ['1\n', 1], ['-1', -1],
      ['1e-30', 0], ['1e29', null], ['1,5', null], ['0x10', null], ['', null], ['.', null], ['1e', null]]
    for (const [text, read] of reads) expect(serverPlanNumber(text), show(text)).toBe(read)
    expect(planNumber('1e2')).toBeNull()
  })
  it('writes numbers into plan fields without an exponent', () => {
    expect(formatPlanNumber(5e-7, 8)).toBe('0.0000005')
    expect(formatPlanNumber(0.000000049, 8)).toBe('0.00000005')
    expect(formatPlanNumber(1.5, 2)).toBe('1.5')
    expect(formatPlanNumber(-0, 2)).toBe('0')
    expect(formatPlanNumber(1e23, 4)).toBe('100000000000000000000000')
    expect(formatPlanNumber(Infinity, 2)).toBe('')
    for (const value of [5e-7, 1e23, 123.456789]) expect(isPlanText(formatPlanNumber(value, 8)), String(value)).toBe(true)
  })
})
