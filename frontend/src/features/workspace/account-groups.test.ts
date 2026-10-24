import { describe, expect, it } from 'vitest'
import type { BrokerAccount } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { addressKey, groupAccounts, groupBalance } from './account-groups'

const HL = '0xAbCdEf0123456789abcdef0123456789ABCDEF01'
const account = (id: string, patch: Partial<BrokerAccount> = {}): BrokerAccount => ({ ...accountFixture, id, name: id, ...patch })
const hl = (id: string, patch: Partial<BrokerAccount> = {}) => account(id, { venueId: 'hyperliquid', address: HL.toLowerCase(), sourceId: HL.toLowerCase(), balanceUsd: '100', ...patch })
const risex = (id: string, patch: Partial<BrokerAccount> = {}) => account(id, { venueId: 'risex', address: HL.toLowerCase(), sourceId: HL.toLowerCase(), balanceUsd: '50', ...patch })
const ids = (accounts: BrokerAccount[]) => accounts.map(a => a.id)

it('groups Solana public source IDs even with a null legacy address, preserving distinct case-sensitive keys', () => {
  const source = 'So11111111111111111111111111111111111111112'
  const groups = groupAccounts([
    account('sol-a', { venueId: 'synthetic-solana', address: null, sourceId: source }),
    account('sol-b', { venueId: 'synthetic-solana', address: null, sourceId: source.toLowerCase() }),
    account('sol-c', { venueId: 'synthetic-solana', address: null, sourceId: source }),
    account('index', { address: null, sourceId: '123' }),
  ], 'address')
  expect(groups.map(g => g.key)).toEqual([`address:${source}`, `address:${source.toLowerCase()}`, 'no-address'])
  expect(ids(groups[0]!.accounts)).toEqual(['sol-a', 'sol-c'])
})

describe('addressKey', () => {
  it('compares EVM addresses without case and everything else as written', () => {
    expect(addressKey(HL)).toBe(HL.toLowerCase())
    expect(addressKey(`  ${HL.toUpperCase().replace('0X', '0x')} `)).toBe(HL.toLowerCase())
    expect(addressKey('SomeOtherId')).toBe('SomeOtherId')
    expect(addressKey('someotherid')).toBe('someotherid')
  })
  it.each([[null], [undefined], [''], ['   ']])('has no key for %j', value => { expect(addressKey(value)).toBeNull() })
})

describe('groupAccounts by venue', () => {
  const list = [hl('h1'), risex('r1'), hl('h2', { address: '0x' + '2'.repeat(40) }), account('manual'), risex('r2')]
  it('makes one group per venue in order of first appearance, keeping member order', () => {
    const groups = groupAccounts(list, 'venue')
    expect(groups.map(g => g.key)).toEqual(['venue:hyperliquid', 'venue:risex', 'venue:manual'])
    expect(groups.map(g => g.kind === 'venue' && g.venueId)).toEqual(['hyperliquid', 'risex', 'manual'])
    expect(groups.map(g => ids(g.accounts))).toEqual([['h1', 'h2'], ['r1', 'r2'], ['manual']])
    expect(groups.map(g => g.venueIds)).toEqual([['hyperliquid'], ['risex'], ['manual']])
  })
  it('partitions the input without mutating it', () => {
    const copy = [...list]
    const groups = groupAccounts(list, 'venue')
    expect(list).toEqual(copy)
    expect(groups.flatMap(g => ids(g.accounts)).sort()).toEqual(ids(list).sort())
  })
  it('returns nothing for no accounts', () => { expect(groupAccounts([], 'venue')).toEqual([]) })
})

describe('groupAccounts by address', () => {
  it('merges the same address across venues and ignores EVM case', () => {
    const groups = groupAccounts([hl('h1', { address: HL }), risex('r1', { address: HL.toLowerCase() }), hl('h2', { address: HL.toUpperCase().replace('0X', '0x') })], 'address')
    expect(groups).toHaveLength(1)
    const [group] = groups
    expect(group).toMatchObject({ kind: 'address', key: `address:${HL.toLowerCase()}`, venueIds: ['hyperliquid', 'risex'] })
    // Display never depends on which spelling came first or which members a filter leaves visible.
    expect(group?.kind === 'address' && group.address).toBe(HL.toLowerCase())
    expect(ids(group!.accounts)).toEqual(['h1', 'r1', 'h2'])
  })
  it('keeps different addresses apart in order of first appearance', () => {
    const other = '0x' + '9'.repeat(40)
    const groups = groupAccounts([hl('a', { address: other }), hl('b'), risex('c', { address: other })], 'address')
    expect(groups.map(g => ids(g.accounts))).toEqual([['a', 'c'], ['b']])
    expect(groups.map(g => g.venueIds)).toEqual([['hyperliquid', 'risex'], ['hyperliquid']])
  })
  it('puts accounts without an address in one last group, even when they come first', () => {
    const groups = groupAccounts([
      account('manual'), hl('h1'),
      // Lighter stores an account index, never the owner address, so it cannot be matched to a wallet.
      account('lighter', { venueId: 'lighter', address: null, sourceId: '48213' }),
      hl('blank', { address: '  ' }),
    ], 'address')
    expect(groups.map(g => g.kind)).toEqual(['address', 'no-address'])
    expect(groups[1]).toMatchObject({ key: 'no-address', venueIds: ['manual', 'lighter', 'hyperliquid'] })
    expect(ids(groups[1]!.accounts)).toEqual(['manual', 'lighter', 'blank'])
  })
  it('does not let a literal "none" address collide with the no-address group', () => {
    const groups = groupAccounts([account('a', { address: 'none' }), account('b')], 'address')
    expect(groups.map(g => g.key)).toEqual(['address:none', 'no-address'])
  })
  it('compares non-EVM identifiers exactly', () => {
    const groups = groupAccounts([account('a', { address: 'Abc' }), account('b', { address: 'abc' })], 'address')
    expect(groups).toHaveLength(2)
  })
})

describe('groupBalance', () => {
  it('sums enabled balances exactly', () => {
    expect(groupBalance([hl('a', { balanceUsd: '0.1' }), hl('b', { balanceUsd: '0.2' })])).toEqual({ kind: 'exact', value: '0.3', enabled: 2, accounts: 2 })
    expect(groupBalance([hl('a', { balanceUsd: '1250.123456' }), hl('b', { balanceUsd: '-250.123456' })])).toMatchObject({ kind: 'exact', value: '1000' })
  })
  it('leaves disabled accounts out and says how many were counted', () => {
    expect(groupBalance([hl('a', { balanceUsd: '10' }), hl('b', { balanceUsd: '99', isEnabled: false })])).toEqual({ kind: 'exact', value: '10', enabled: 1, accounts: 2 })
  })
  it('is partial, never a sum, when an enabled account has no balance', () => {
    expect(groupBalance([hl('a', { balanceUsd: '10' }), hl('b', { balanceUsd: null }), hl('c', { balanceUsd: null })])).toEqual({ kind: 'partial', reported: 1, enabled: 3, accounts: 3 })
    const withoutBalance = hl('d')
    delete withoutBalance.balanceUsd
    expect(groupBalance([withoutBalance])).toEqual({ kind: 'partial', reported: 0, enabled: 1, accounts: 1 })
  })
  it('is none when every account is disabled', () => {
    expect(groupBalance([hl('a', { isEnabled: false })])).toEqual({ kind: 'none', accounts: 1 })
  })
  it('treats a missing enabled flag as enabled and keeps exact zero', () => {
    const legacy = hl('a', { balanceUsd: '0' })
    delete legacy.isEnabled
    expect(groupBalance([legacy])).toMatchObject({ kind: 'exact', value: '0', enabled: 1 })
  })
})
