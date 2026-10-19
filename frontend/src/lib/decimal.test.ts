import { describe, expect, it } from 'vitest'
import { decimalDifference, decimalLess, decimalSign } from './decimal'

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

describe('exact plain decimal difference', () => {
  it.each([
    ['1.25', '0.5', '0.75'],
    ['0.5', '0', '0.5'],
    ['1', '1.000', '0'],
    ['0.1', '0.100000000000000001', '-0.000000000000000001'],
    ['9007199254740993', '0.5', '9007199254740992.5'],
    ['-1.5', '0.25', '-1.75'],
    ['10', '2.50', '7.5'],
  ])('%s minus %s is exactly %s', (a, b, expected) => {
    expect(decimalDifference(a, b)).toBe(expected)
  })
  it.each([['1e2', '1'], ['', '1'], ['1', '.5'], ['1', '+1'], ['1', ' 1']])('refuses %j minus %j as unknown', (a, b) => {
    expect(decimalDifference(a, b)).toBeNull()
  })
})
