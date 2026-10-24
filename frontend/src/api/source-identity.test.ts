import { describe, expect, it } from 'vitest'
import { isSolanaAddress } from './source-identity'

describe('Solana source identity', () => {
  it.each(['11111111111111111111111111111111', 'So11111111111111111111111111111111111111112',
    'BrZp5bidJ3WUvceSq7X78bhjTfZXeezzGvGEV4hAYKTa', '1'.repeat(31) + '2'])('decodes %s to exactly 32 bytes', value => {
    expect(isSolanaAddress(value)).toBe(true)
  })
  it.each(['', '1'.repeat(31), '1'.repeat(33), 'z'.repeat(32), 'z'.repeat(44),
    '0x' + '1'.repeat(40), 'O'.repeat(44), 'I'.repeat(44), 'l'.repeat(44),
    ' So11111111111111111111111111111111111111112', 'So11111111111111111111111111111111111111112\n',
    '１'.repeat(32)])('rejects invalid alphabet or decoded size: %s', value => {
    expect(isSolanaAddress(value)).toBe(false)
  })
})
