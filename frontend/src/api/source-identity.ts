/** No wallet SDK is needed to validate a case-sensitive base58-encoded 32-byte public identity. */
export function isSolanaAddress(value: string): boolean {
  if (value.length < 32 || value.length > 44) return false
  const alphabet = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz'
  let number = 0n
  for (const character of value) {
    const digit = alphabet.indexOf(character)
    if (digit < 0) return false
    number = number * 58n + BigInt(digit)
  }
  let bytes = value.match(/^1*/)?.[0].length ?? 0
  while (number > 0n) { bytes++; number >>= 8n }
  return bytes === 32
}
