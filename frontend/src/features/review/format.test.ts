import { describe, expect, it } from 'vitest'
import { reviewMoney, reviewMultiple, reviewPercent } from '@/lib/format'
import { decimalLess } from '@/lib/decimal'
import { negative, reviewR, tone } from './format'

describe('Exact report formatting', () => {
  it('never converts authoritative money to a floating point number', () => {
    expect(reviewMoney('9007199254740993.25')).toBe('$9,007,199,254,740,993.25')
    expect(reviewMoney('-9007199254740993.25', true)).toBe('-$9,007,199,254,740,993.25')
    expect(reviewMoney('0.123456')).toBe('$0.1235')
  })
  it('distinguishes unknown from a genuine zero', () => {
    expect(reviewMoney(null)).toBe('N/A')
    expect(reviewMoney('0', true)).toBe('$0')
    expect(reviewPercent(null)).toBe('N/A')
    expect(reviewMultiple(null)).toBe('N/A')
    expect(reviewPercent('0.5', true)).toBe('50.0%')
    expect(reviewPercent('2.25')).toBe('2.3%')
    expect(reviewMultiple('2.345')).toBe('2.35×')
  })
  it('prints R as the Play receipt does (#119)', () => {
    expect(reviewR(null)).toBe('N/A')
    expect(reviewR('1.98')).toBe('+1.98R')
    expect(reviewR('-0.5')).toBe('-0.50R')
    expect(reviewR('0')).toBe('0.00R')
    expect(reviewR('-0.001')).toBe('−<0.01R')
  })
  it('compares caption extrema without losing fractional precision', () => {
    expect(decimalLess('-9007199254740992.51', '-9007199254740992.50')).toBe(true)
    expect(decimalLess('-0.01', '0')).toBe(true)
    expect(decimalLess('1.01', '1.1')).toBe(true)
    expect(decimalLess('1.00', '1')).toBe(false)
  })
  it('uses exact signs and only suppresses genuine zero loss magnitudes', () => {
    expect(tone(null)).toBe('')
    expect(tone('-000.00')).toBe('')
    expect(negative(null)).toBeNull()
    expect(negative('00.00')).toBe('0')
    const tiny = `0.${'0'.repeat(324)}1`
    expect(tone(tiny)).toBe('positive')
    expect(tone(`-${tiny}`)).toBe('negative')
    expect(negative(tiny)).toBe(`-${tiny}`)
  })
})
