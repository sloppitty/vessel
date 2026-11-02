import { describe, expect, it } from 'vitest'
import { attachmentName } from './download'

describe('attachment names', () => {
  it('reads the server name and keeps only safe characters', () => {
    expect(attachmentName('attachment; filename=vessel-review-month-20261009.csv; filename*=UTF-8\'\'x', 'f.csv')).toBe('vessel-review-month-20261009.csv')
    expect(attachmentName('attachment; filename="../../etc/passwd"', 'f.csv')).toBe('.._.._etc_passwd')
    expect(attachmentName(null, 'f.csv')).toBe('f.csv')
    expect(attachmentName('inline', 'f.csv')).toBe('f.csv')
  })
})
