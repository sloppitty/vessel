import type { BrokerAccount } from '@/api/workspace'
import type { AccountGroupBy } from './account-filters'
import { sumDecimals } from './overview-allocation'
import { isSolanaAddress } from '@/api/source-identity'

/**
 * A group's balance is a total only when every enabled member reports one. Disabled accounts are left out, as in the
 * portfolio tiles and Overview; a partial sum is never presented as the group's balance.
 */
export type GroupBalance =
  | { kind: 'exact'; value: string; enabled: number; accounts: number }
  | { kind: 'partial'; reported: number; enabled: number; accounts: number }
  | { kind: 'none'; accounts: number }

interface Common {
  /** Stable across filter changes: `venue:<id>`, `address:<lowercase EVM address>` or the single `no-address` bucket. */
  key: string
  /** Distinct venues of the members, in order of first appearance. */
  venueIds: string[]
  accounts: BrokerAccount[]
  balance: GroupBalance
}
export type AccountGroup = Common & (
  | { kind: 'venue'; venueId: string }
  /** The comparison form (lowercase for EVM), so the label never depends on which member is visible or first. */
  | { kind: 'address'; address: string }
  /** Manual records and account-index venues (Lighter) store no wallet address. */
  | { kind: 'no-address' }
)

const evmAddress = /^0x[\da-f]{40}$/i
/** The comparison key of an owner address: EVM addresses ignore case, anything else is compared as written. */
export function addressKey(address: string | null | undefined): string | null {
  const text = address?.trim()
  if (!text) return null
  return evmAddress.test(text) ? text.toLowerCase() : text
}

export function groupBalance(accounts: BrokerAccount[]): GroupBalance {
  const enabled = accounts.filter(a => a.isEnabled !== false)
  if (!enabled.length) return { kind: 'none', accounts: accounts.length }
  const values = enabled.flatMap(a => a.balanceUsd == null ? [] : [a.balanceUsd])
  return values.length === enabled.length
    ? { kind: 'exact', value: sumDecimals(values), enabled: enabled.length, accounts: accounts.length }
    : { kind: 'partial', reported: values.length, enabled: enabled.length, accounts: accounts.length }
}

type Bucket = { key: string; accounts: BrokerAccount[] } & (
  | { kind: 'venue'; venueId: string } | { kind: 'address'; address: string } | { kind: 'no-address' })

/**
 * Clusters the given accounts by venue or by owner address. Groups follow the order in which their first member
 * appears (so Venue groups match the venue chips), members keep their order, and the no-address group is always last.
 * Callers filter first: a group exists only when it has a visible member.
 */
export function groupAccounts(accounts: BrokerAccount[], by: Exclude<AccountGroupBy, 'none'>): AccountGroup[] {
  const buckets = new Map<string, Bucket>()
  for (const account of accounts) {
    const publicAddress = account.address ?? (account.sourceId && isSolanaAddress(account.sourceId) ? account.sourceId : null)
    const owner = by === 'address' ? addressKey(publicAddress) : null
    const key = by === 'venue' ? `venue:${account.venueId}` : owner === null ? 'no-address' : `address:${owner}`
    let bucket = buckets.get(key)
    if (!bucket) {
      bucket = by === 'venue' ? { key, kind: 'venue', venueId: account.venueId, accounts: [] }
        : owner === null ? { key, kind: 'no-address', accounts: [] }
          : { key, kind: 'address', address: owner, accounts: [] }
      buckets.set(key, bucket)
    }
    bucket.accounts.push(account)
  }
  const ordered = [...buckets.values()].sort((a, b) => Number(a.kind === 'no-address') - Number(b.kind === 'no-address'))
  return ordered.map(bucket => ({
    ...bucket, venueIds: [...new Set(bucket.accounts.map(a => a.venueId))], balance: groupBalance(bucket.accounts),
  }))
}
