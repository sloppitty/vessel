import type { AccountCredential, AccountDiscovery } from '@/api/account-access'
import type { VenueInfo } from '@/api/system'
import { accountFixture } from './workspace-fixture'
import { systemFixture } from './system-fixture'

export const indexVenue: VenueInfo = {
  ...systemFixture.venues[0]!, id: 'lighter', name: 'Lighter', source: 'account-index', logo: 'lighter',
  credentialSetupUrl: 'https://app.lighter.xyz/read-only-tokens',
  capabilities: { ...systemFixture.venues[0]!.capabilities, accountDiscovery: true, readOnlyCredential: true, stream: false },
}
export const indexAccount = {
  ...accountFixture, venueId: indexVenue.id, name: 'Main account', sourceId: '9007199254740993', address: null,
}
export const discoveryFixture: AccountDiscovery = {
  venueId: indexVenue.id, address: `0x${'12'.repeat(20)}`, notice: 'Account values are venue-reported USD.',
  accounts: [
    { sourceId: indexAccount.sourceId, name: 'Main account', accountType: 'main', accountValueUsd: '123.123456789', collateralUsd: null, availableBalanceUsd: null, existingAccountId: null, isEnabled: null },
    { sourceId: '9223372036854775807', name: 'Subaccount 1', accountType: 'subaccount', accountValueUsd: null, collateralUsd: null, availableBalanceUsd: null, existingAccountId: null, isEnabled: null },
  ],
}
export const credentialFixture: AccountCredential = {
  storageConfigured: true,
  credential: { scope: 'all', expiresAt: '2027-10-01T10:00:00Z', lastVerifiedAt: '2026-10-07T10:00:00Z', status: 'valid' },
}
