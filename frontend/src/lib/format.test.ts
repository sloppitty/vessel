import { expect, it } from 'vitest'
import { amount, money, reviewDate, reviewMoney, reviewMultiple, reviewPercent, reviewResult, shortAddress, shortSource } from './format'

it('keeps unavailable values distinct from a reported zero', () => {
  expect(money(null)).toBe('—')
  expect(money('0')).toBe('$0.00')
})
it('does not display tiny real prices or quantities as zero', () => {
  expect(money('0.000000045')).toBe('$0.000000045')
  expect(amount('0.00000000000000000001')).toBe('0.00000000000000000001')
})
it('does not silently change amounts beyond safe number precision', () => {
  expect(money('1000000000000000.05')).toBe('$1,000,000,000,000,000.05')
  expect(money('9007199254740990.99')).toBe('$9,007,199,254,740,990.99')
  expect(money('79228162514264337593543950335')).toBe('$79,228,162,514,264,337,593,543,950,335.00')
  expect(money('100000.493827156049382715604936')).toBe('$100,000.49')
  expect(amount('79228162514264337593543950335')).toBe('79,228,162,514,264,337,593,543,950,335')
  expect(amount('123456789012.123456789')).toBe('123,456,789,012.12345679')
})
it('formats negative exact totals with the same grouping', () => {
  expect(money('-79228162514264337593543950336.25')).toBe('-$79,228,162,514,264,337,593,543,950,336.25')
  expect(money('-12.5')).toBe('-$12.50')
  expect(amount('-0.000000001234')).toBe('-0.000000001234')
})
it('preserves distinct workspace and Review money outputs', () => {
  expect(money(null)).toBe('—')
  expect(reviewMoney(null)).toBe('N/A')
  expect(money('0')).toBe('$0.00')
  expect(reviewMoney('0', true)).toBe('$0')
  expect(money('0.123456')).toBe('$0.12')
  expect(reviewMoney('0.123456')).toBe('$0.1235')
  expect(money('0.000000045')).toBe('$0.000000045')
  expect(reviewMoney('0.000000045')).toBe('$0')
  expect(reviewMoney('9007199254740993.25', true)).toBe('+$9,007,199,254,740,993.25')
  expect(reviewMoney('-0.0001', true)).toBe('-$0.0001')
})
it('keeps a tiny per-Play result signed and visible, and only an exact zero as $0', () => {
  // Buy 0.001 at 100, sell at 99.99: the Play-local result before fees is -0.00001, not a scratch.
  expect(reviewMoney('-0.00001', true)).toBe('$0')
  expect(reviewResult('-0.00001')).toBe('-$0.00001')
  expect(reviewResult('0.00001')).toBe('+$0.00001')
  expect(reviewResult('-0.0000000000000000000000000001')).toBe('-$0.0000000000000000000000000001')
  expect(reviewResult('0.00009999')).toBe('+$0.00009999')
  expect(reviewResult('0')).toBe('$0')
  expect(reviewResult('-0.0001')).toBe('-$0.0001')
  expect(reviewResult('200.123456789')).toBe('+$200.12')
  expect(reviewResult(null)).toBe('N/A')
})
it('keeps Review percent, ratio and UTC dates separate from local refresh dates', () => {
  expect(reviewPercent('0.5', true)).toBe('50.0%')
  expect(reviewPercent('0.5')).toBe('0.5%')
  expect(reviewMultiple('2')).toBe('2.00×')
  expect(reviewDate('2026-10-14T23:30:00-02:00')).toBe('15 Oct')
  expect(reviewDate('2024-02-15T12:00:00Z', true)).toBe('15 Feb 2024')
  expect(shortAddress(null)).toBe('Manual record')
  expect(shortAddress('0x1234567890abcdef')).toBe('0x1234…cdef')
})
it('shortens source identities without losing their kind', () => {
  expect(shortSource(`0x${'ab'.repeat(20)}`)).toBe('0xabab…abab')
  expect(shortSource('42')).toBe('#42')
  expect(shortSource('9007199254740993')).toBe('#9007…0993')
  expect(shortSource(null)).toBeNull()
  expect(shortSource('')).toBeNull()
})
