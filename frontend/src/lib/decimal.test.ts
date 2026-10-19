import { describe, expect, it } from 'vitest'
import {
  decimalLess, decimalSign, exactAdd, exactCompare, exactDecimal, exactDiv, exactFixed, exactInteger, exactMul, exactNumber, exactSignOf, exactSub,
  exactZero,
} from './decimal'

describe('exact plain decimals', () => {
  it.each([null, '0', '-0', '00.000', '-000.000'])('keeps %j neutral', value => {
    expect(decimalSign(value)).toBe(0)
  })
  it.each(['9007199254740993.25', '0.0000000000000000000000000001', `0.${'0'.repeat(324)}1`])('keeps the exact sign of %s', value => {
    expect(decimalSign(value)).toBe(1)
    expect(decimalSign(`-${value}`)).toBe(-1)
  })
  it.each([
    ['-9007199254740992.51', '-9007199254740992.50', true],
    ['9007199254740992.50', '9007199254740992.51', true],
    ['-0.01', '0', true],
    ['1.01', '1.1', true],
    ['1.00', '1', false],
    ['-0.00', '0', false],
    ['-000.0001', '-0.001', false],
    [`0.${'0'.repeat(324)}1`, '0', false],
  ])('compares %s with %s without rounding', (a, b, expected) => {
    expect(decimalLess(a, b)).toBe(expected)
  })
})

describe('exact values', () => {
  const x = (text: string) => exactDecimal(text)!
  it('parses plain decimals only, in lowest terms', () => {
    expect(x('0.50')).toEqual({ n: 1n, d: 2n })
    expect(x('-12.5')).toEqual({ n: -25n, d: 2n })
    for (const text of ['', '1e2', '.5', '+1', '5.', ' 1']) expect(exactDecimal(text)).toBeNull()
  })

  it('adds, multiplies and divides without rounding', () => {
    // 0.1 + 0.2 is 0.3 exactly; in floats it is not.
    expect(exactCompare(exactAdd(x('0.1'), x('0.2')), x('0.3'))).toBe(0)
    expect(exactFixed(exactMul(x('3'), exactSub(x('100.3'), x('100'))), 28)).toBe('0.9')
    const third = exactDiv(x('10'), exactInteger(3))!
    expect(exactCompare(exactMul(third, exactInteger(3)), x('10'))).toBe(0)
    expect(exactDiv(x('1'), exactZero)).toBeNull()
  })

  it('rounds once, half away from zero, and never prints a sign the value lacks', () => {
    expect(exactFixed(x('2.345'), 2)).toBe('2.35')
    expect(exactFixed(x('-2.345'), 2)).toBe('-2.35')
    expect(exactFixed(x('2.344'), 2)).toBe('2.34')
    expect(exactFixed(exactDiv(x('1'), exactInteger(3))!, 2)).toBe('0.33')
    expect(exactFixed(x('-0.004'), 2)).toBe('0')
    expect(exactSignOf(x('-0.004'))).toBe(-1)
    expect(exactFixed(x('5.000'), 2)).toBe('5')
    expect(exactNumber(x('-0.25'))).toBe(-0.25)
  })
})
