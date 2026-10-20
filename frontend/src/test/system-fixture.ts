import type { SystemInfo, VenueCapabilities } from '@/api/system'

const none: VenueCapabilities = { sync: false, instruments: false, orders: false, candles: false, marketContext: false, stream: false, stablecoinWallet: false }

// The shared /api/system contract, not a simulated broker connection.
export const systemFixture: SystemInfo = {
  application: 'Vessel', stage: 'foundation',
  owner: { id: '11111111-1111-1111-1111-111111111111', displayName: 'Owner' },
  marketScope: 'perpetuals', allowsConcurrentPlays: true,
  venues: [
    { id: 'hyperliquid', name: 'Hyperliquid', status: 'read-only', source: 'evm-address', quoteAsset: 'USDC', logo: 'hyperliquid',
      tradeUrlTemplate: 'https://app.hyperliquid.xyz/trade/{instrument}', priceRule: 'significant-figures',
      intervals: ['1m', '3m', '5m', '15m', '30m', '1h', '2h', '4h', '8h', '12h', '1d', '3d', '1w', '1M'],
      capabilities: { sync: true, instruments: true, orders: true, candles: true, marketContext: true, stream: true, stablecoinWallet: true, currentOrderBook: true } },
    // A second Lighter exchange: same capabilities as Lighter, USDG collateral and its own logo.
    { id: 'lighter-rh', name: 'Lighter · Robinhood', status: 'read-only', source: 'account-index', quoteAsset: 'USDG', logo: 'lighter-rh',
      tradeUrlTemplate: 'https://robinhoodchain.lighter.xyz/trade/{instrument}', credentialSetupUrl: 'https://robinhoodchain.lighter.xyz',
      priceRule: 'tick-size', intervals: ['1m', '5m', '15m', '30m', '1h', '4h', '12h', '1d', '1w'],
      capabilities: { sync: true, instruments: true, orders: true, candles: true, marketContext: false, stream: false, stablecoinWallet: false, accountDiscovery: true, readOnlyCredential: true, currentOrderBook: true } },
    // No logo key: shows the generic icon.
    { id: 'quantfury', name: 'Quantfury', status: 'candidate', source: 'none', quoteAsset: null, tradeUrlTemplate: null, intervals: [], priceRule: 'significant-figures', capabilities: none },
    { id: 'manual', name: 'Manual', status: 'manual', source: 'none', quoteAsset: null, logo: 'manual', tradeUrlTemplate: null, intervals: [], priceRule: 'significant-figures', capabilities: none },
  ],
}
