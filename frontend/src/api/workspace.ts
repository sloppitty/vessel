import { isPaperSettings, isPaperPlayState, type PaperSettings, type PaperPlayState, type TradingMode } from './paper'
import { overviewSummaryPath, isOverviewSummary, type OverviewQuery, type OverviewSummaryDocument } from './overview-summary'
import { activityFillsPath, activityPath, isActivityFills, isActivityPage, type ActivityFills, type ActivityPage, type ActivityQuery } from './activity'
import { readSseStream, SseOverflowError, SseParser } from './sse'
import { isAfter, isNotificationAfter, isNotificationCursor, isRequestCursor, isUnknownCursor } from './event-cursor'
import {
  isInAppNotification, isNotificationClaim, isNotificationHead, isNotificationPage, isNotificationPreferenceFields,
  isNotificationPreferences, isNotificationReadAll, notificationPagePath, notificationPreferencesConflict, notificationReadAllConflict,
  type InAppNotification, type NotificationClaim, type NotificationHead, type NotificationPage, type NotificationPreferenceFields,
  type NotificationPreferences, type NotificationQuery,
} from './notifications'
import { isAccountMonitor, isMonitorEventPage, isMonitorRunAccepted, isMonitorSummary, monitorFrame,
  type AccountMonitor, type MonitorEventPage, type MonitorRunAccepted, type MonitorSummary, type MonitorStreamEvent } from './monitor'
import { candleIntervals, type CandleInterval } from './candles'
import { ApiError } from './errors'
import { createApiCall, type ApiCredentials, type AuthFailure } from './request'
import { authErrorMessage } from './auth'
import { emitApiEvent } from './events'
import { isImportedFill, type ImportedFill } from './fills'
import {
  isCount, isFilledText, isGuid, isNonNegativeDecimal, isNullOr, isPlainDecimal, isPositiveCount, isRecord, isText, isTimestamp,
} from './guards'
import {
  accountIndex, credentialSaveMessage, credentialTransportAllowed, credentialTransportMessage,
  type AccountCredential, type AccountDiscovery,
} from './account-access'
import {
  isNextAction, isPlayCounts, isPlayNote, isPlayExecution, isPlayHistory, isPlayList, isSavedEvidence, isSavedPlay, playCountsPath, playListPath,
  type LinkOrder, type NextAction, type PlayNote, type PlayNoteState, type RebindLevel, type PlayCounts, type PlayExecution, type PlayFields, type PlayHistory, type PlayList, type PlayListQuery, type SavedEvidence, type SavedPlay, type StatusRequest, type UnresolvedReason,
} from './plays'
import { isSizingDocument, type SizingDocument } from './sizing'
import { isReviewDocument, reviewPath, isReviewExclusions, reviewExclusionsPath, type ReviewDocument, type ReviewQuery, type ReviewExclusions } from './review'
import {
  isRetrospectivePreview, isCreateRetrospectivePlayRequest, retrospectivePreviewPath, retrospectivePlayPath,
  type RetrospectivePreview, type CreateRetrospectivePlayRequest,
} from './retrospective'
import {
  isManualExecutions, isManualExecutionInput, isManualExecutionCorrection,
  type ManualExecutions, type ManualExecutionInput, type ManualExecutionCorrection,
} from './diary'
import type { ImageMarkup } from './markup'

export interface Portfolio {
  tradingMode?: TradingMode
  id: string
  name: string
  accountCount: number
  totalValueUsd: string | null
  valueCoverage: 'complete' | 'partial' | 'unavailable'
  /** Sum of account balances (perps plus stablecoin wallet, or the wallet alone in unified modes). */
  balanceUsd?: string | null
  balanceCoverage?: 'complete' | 'partial' | 'unavailable'
  availableBalanceUsd?: string | null
  availableBalanceAccountCount?: number
  availableBalanceCoverage?: 'complete' | 'partial' | 'unavailable'
  enabledAccountCount?: number
  /** Icon key chosen by the owner; absent from servers that predate portfolio icons. */
  icon?: string
}
export interface VenueInstrument {
  contractId: string
  quantityDecimals: number
  maxLeverage: number
  /** Asset prices are quoted and margined in, e.g. USDC for BTC/USDC. */
  quoteAsset: string
  /** Price tick when the venue has one; prices placed on the chart round to it. */
  priceStep?: number | null
  /** Market category such as stocks or commodity; absent for venues that do not label markets. */
  category?: string | null
  /** The venue's own identifier when it differs from the contract key, e.g. a numeric market ID. */
  venueContractId?: string | null
  /** Maintenance margin as a fraction of notional when the venue states it; liquidation estimates otherwise assume one. */
  maintenanceMarginFraction?: number | null
}
export interface InstrumentCatalog {
  venueId: string
  marketScope: 'perpetuals'
  scope: 'venue-perpetuals' | 'manual'
  instruments: VenueInstrument[]
  notice: string
}
/** Venue trade candle. OHLCV stay exact decimal strings; times are UTC Unix milliseconds. */
export interface VenueCandle {
  openTime: number
  closeTime: number
  open: string
  high: string
  low: string
  close: string
  volume: string
  /** Null when the venue reports no trade count. */
  trades: number | null
}
export interface CandleSeries {
  venueId: string
  instrument: string
  interval: CandleInterval
  priceSource: 'trades'
  candles: VenueCandle[]
  requestedFrom: number
  requestedTo: number
  retrievedAt: string
  historyExhausted: boolean
  notice: string
}
export interface CandleQuery {
  instrument: string
  interval: CandleInterval
  endTime?: number
  refresh?: boolean
}
/** Venue header statistics for one perpetual. Decimal strings are kept exactly as reported. */
export interface MarketContext {
  venueId: string
  instrument: string
  markPrice: string
  oraclePrice: string
  midPrice: string | null
  /** Null when the venue does not report it; the 24-hour change is then not shown. */
  previousDayPrice: string | null
  dayNotionalVolume: string
  openInterest: string
  fundingRate: string
  premium: string | null
  observedAt: string
  notice: string
}
export type LiveStreamState = 'live' | 'reconnecting' | 'stale'
/** Server-side state of the upstream venue feed behind one market stream. */
export interface MarketStreamStatus {
  state: LiveStreamState
  observedAt: string
}
/** One validated market-stream event. Prices are observations, never fills. */
export type MarketStreamEvent =
  | { type: 'candle'; candle: VenueCandle }
  | { type: 'context'; context: MarketContext }
  | { type: 'status'; status: MarketStreamStatus }
export interface MarketStreamQuery {
  instrument: string
  interval: CandleInterval
}
export interface BrokerAccount {
  tradingMode?: TradingMode
  startingCapitalUsd?: string | null
  id: string
  portfolioId: string | null
  name: string
  venueId: string
  address: string | null
  /** Missing on older servers. Display sourceId ?? address for EVM compatibility. */
  sourceId?: string | null
  accountValueUsd: string | null
  lastSyncedAtUtc: string | null
  syncStatus: 'manual' | 'not-synced' | 'synced' | 'error' | 'paper'
  lastSyncError: string | null
  positionCount: number
  historyNotice: string | null
  /** Missing only during rollout from the previous API; treated as enabled. */
  isEnabled?: boolean
  availableStablecoinNominalUsd?: string | null
  stablecoinScope?: string | null
  accountMode?: string | null
  /** Supported stablecoins in the wallet, held amounts included. */
  totalStablecoinNominalUsd?: string | null
  /** Nominal balance: perps plus wallet, or the wallet alone in unified and portfolio-margin modes. */
  balanceUsd?: string | null
  /** Exactly one availability source selected by the server, never summed with another ledger. */
  availableBalanceUsd?: string | null
  availableBalanceSource?: 'wallet' | 'margin' | 'withdrawable' | 'paper' | null
  settingsRevision?: number
}
export interface AccountSettingsExpectation {
  accountId: string
  settingsRevision: number
}
export interface AccountSnapshot {
  observedAtUtc: string
  valueScope: string
  accountValueUsd: string | null
  withdrawableUsd: string | null
  /** Reported available balance/free collateral, not guaranteed withdrawal capacity. */
  availableMarginUsd?: string | null
  marginUsedUsd: string | null
  positions: Array<{
    contractId: string
    signedQuantity: string
    entryPrice: string
    unrealizedPnlUsd: string
    marginUsedUsd: string
    leverage: number | null
  }>
  stablecoinWallet?: StablecoinWallet | null
}
export interface StablecoinWallet {
  observedAtUtc: string
  accountMode: string
  scope: string
  totalNominalUsd: string
  availableNominalUsd: string
  balances: Array<{ symbol: string; tokenIndex: number; tokenId: string; total: string; held: string; available: string }>
  notice: string
}
export interface Overview {
  portfolios: Portfolio[]
  accounts: BrokerAccount[]
  totals: {
    portfolioCount: number
    accountCount: number
    totalAccountValueUsd: string | null
    valuedAccountCount: number
    openPositionCount: number
    importedFillCount: number
    availableStablecoinNominalUsd?: string | null
    stablecoinAccountCount?: number
    totalBalanceUsd?: string | null
    balanceAccountCount?: number
    balanceCoverage?: 'complete' | 'partial' | 'unavailable'
    availableBalanceUsd?: string | null
    availableBalanceAccountCount?: number
    availableBalanceCoverage?: 'complete' | 'partial' | 'unavailable'
    enabledAccountCount?: number
  }
  recentActivity: ImportedFill[]
  scopeNote: string
}
export interface CreateAccount {
  tradingMode?: TradingMode
  startingCapitalUsd?: string
  portfolioId: string | null
  name: string
  venueId: string
  address?: string
  sourceId?: string
  manualAccountValueUsd?: string
}
export interface WorkspaceApi {
  readonly tradingMode?: TradingMode
  forTradingMode?(mode: TradingMode): WorkspaceApi
  paperSettings(signal?: AbortSignal): Promise<PaperSettings>
  savePaperSettings(settings: PaperSettings): Promise<PaperSettings>
  paperState(id: string, signal?: AbortSignal): Promise<PaperPlayState>
  monitor(signal?: AbortSignal): Promise<MonitorSummary>
  accountMonitor(id: string, signal?: AbortSignal): Promise<AccountMonitor>
  runAccountMonitor(id: string, signal?: AbortSignal): Promise<MonitorRunAccepted>
  /** Stored owner events after a cursor. A cursor outside the retained range returns a reset to the head. */
  monitorEvents(after: string, limit: number, signal?: AbortSignal): Promise<MonitorEventPage>
  /** With `notificationsAfter` the stream also carries the owner's notification feed from that cursor. */
  monitorStream(after: string, signal: AbortSignal, onEvent: (event: MonitorStreamEvent) => void, notificationsAfter?: string): Promise<void>
  /** The notification head, unread count, channel switch and notifications worker status from one snapshot. */
  notificationHead(signal?: AbortSignal): Promise<NotificationHead>
  notifications(query: NotificationQuery, signal?: AbortSignal): Promise<NotificationPage>
  /** Claims every unshown unread notification for this tab; only the returned ones pop up. */
  claimNotifications(signal?: AbortSignal): Promise<NotificationClaim>
  /** The notification in its current state; a withdrawn one stays withdrawn. */
  readNotification(id: string, signal?: AbortSignal): Promise<InAppNotification>
  /** Marks read every unread notification that existed at `upTo`. A 409 (reset or ahead of the head) means reload first. */
  readAllNotifications(upTo: string, signal?: AbortSignal): Promise<{ read: number }>
  notificationPreferences(signal?: AbortSignal): Promise<NotificationPreferences>
  /** A stale `expectedRevision` is a 409 with `notificationPreferencesConflict`. */
  saveNotificationPreferences(preferences: NotificationPreferenceFields & { expectedRevision: number }): Promise<NotificationPreferences>
  /** Accepted (202): the test arrives through the stream. A 429 carries the cooldown in `retryAtUtc`. */
  requestTestNotification(): Promise<void>
  dismissPlayDiscrepancy(playId: string, discrepancyId: string, note?: string, signal?: AbortSignal): Promise<void>
  dismissAccountDiscrepancy(accountId: string, discrepancyId: string, note?: string, signal?: AbortSignal): Promise<void>
  overviewSummary(query: OverviewQuery, signal?: AbortSignal): Promise<OverviewSummaryDocument>
  /** One page of position operations with the request's filters, scope and page size. */
  activity(query: ActivityQuery, signal?: AbortSignal): Promise<ActivityPage>
  /** One operation's execution rows, loaded when its row expands. A 404 means the history changed. */
  activityFills(accountId: string, operationId: string, signal?: AbortSignal): Promise<ActivityFills>
  review(query: ReviewQuery, signal?: AbortSignal): Promise<ReviewDocument>
  reviewExclusions(query: ReviewQuery, signal?: AbortSignal): Promise<ReviewExclusions>
  retrospectivePreview(accountId: string, operationId: string, signal?: AbortSignal): Promise<RetrospectivePreview>
  createRetrospectivePlay(body: CreateRetrospectivePlayRequest, signal?: AbortSignal): Promise<SavedPlay>
  overview(signal?: AbortSignal): Promise<Overview>
  portfolios(signal?: AbortSignal): Promise<Portfolio[]>
  accounts(signal?: AbortSignal): Promise<BrokerAccount[]>
  account(id: string, signal?: AbortSignal): Promise<BrokerAccount>
  instruments(id: string, signal?: AbortSignal): Promise<InstrumentCatalog>
  candles(id: string, query: CandleQuery, signal?: AbortSignal): Promise<CandleSeries>
  marketContext(id: string, instrument: string, signal?: AbortSignal): Promise<MarketContext>
  /**
   * Reads live candles, statistics and feed status until the server ends the stream (resolves), the signal
   * aborts (resolves) or the connection fails (rejects with ApiError). Invalid events are ignored.
   */
  marketStream(id: string, query: MarketStreamQuery, signal: AbortSignal, onEvent: (event: MarketStreamEvent) => void): Promise<void>
  createPortfolio(name: string, icon?: string, accountIds?: string[], expected?: AccountSettingsExpectation[]): Promise<Portfolio>
  moveAccounts(accountIds: string[], portfolioId: string | null, expected?: AccountSettingsExpectation[]): Promise<BrokerAccount[]>
  createAccount(account: CreateAccount): Promise<BrokerAccount>
  discoverAccounts(venueId: string, address: string, signal?: AbortSignal, token?: string): Promise<AccountDiscovery>
  accountCredential(id: string, signal?: AbortSignal): Promise<AccountCredential>
  saveAccountCredential(id: string, token: string): Promise<AccountCredential>
  deleteAccountCredential(id: string): Promise<void>
  updatePortfolio(id: string, settings: { name: string; icon?: string }): Promise<Portfolio>
  deletePortfolio(id: string): Promise<void>
  updateAccount(id: string, settings: { name: string; portfolioId: string | null; isEnabled: boolean; expectedRevision: number }): Promise<BrokerAccount>
  deleteAccount(id: string): Promise<void>
  snapshot(id: string, signal?: AbortSignal): Promise<AccountSnapshot | null>
  fills(id: string, signal?: AbortSignal): Promise<ImportedFill[]>
  sync(id: string, signal?: AbortSignal): Promise<BrokerAccount>
  /** One view of saved Plays: Drafts and In progress in full (at most 200), the others paged. */
  plays(query: PlayListQuery, signal?: AbortSignal): Promise<PlayList>
  /** Rows in every view for the same search and scope. */
  playCounts(query: { q?: string; scope?: string }, signal?: AbortSignal): Promise<PlayCounts>
  play(id: string, signal?: AbortSignal): Promise<SavedPlay>
  createPlay(fields: PlayFields): Promise<SavedPlay>
  /** A plan change after planning needs `revisionReason`. A stale `expectedVersion` is a 409. */
  updatePlay(id: string, fields: PlayFields & { expectedVersion: number; revisionReason?: string }): Promise<SavedPlay>
  changePlayStatus(id: string, expectedVersion: number, request: StatusRequest): Promise<SavedPlay>
  playHistory(id: string, signal?: AbortSignal): Promise<PlayHistory>
  /** Complete review, Review again and No review needed on a Play the owner last saw at `expectedVersion` (phase 1, 1.2). */
  completeReview(id: string, expectedVersion: number): Promise<SavedPlay>
  reopenReview(id: string, expectedVersion: number): Promise<SavedPlay>
  noReviewNeeded(id: string, expectedVersion: number): Promise<SavedPlay>
  /** Declare an Open Play with imported executions unresolved, or return an Unresolved one to Open (phase 1, 1.3a). */
  declareUnresolved(id: string, expectedVersion: number, reason: UnresolvedReason, note: string): Promise<SavedPlay>
  returnToOpen(id: string, expectedVersion: number, note: string): Promise<SavedPlay>
  /** A Play's append-only in-trade log, oldest first. */
  playNotes(id: string, signal?: AbortSignal): Promise<PlayNote[]>
  addPlayNote(id: string, request: { requestId: string; text: string; state: PlayNoteState | null }): Promise<PlayNote[]>
  /** The owner's most recent next actions from ended Plays, newest first, at most ten (phase 1, 1.5). */
  nextActions(signal?: AbortSignal): Promise<NextAction[]>
  manualExecutions(id: string, signal?: AbortSignal): Promise<ManualExecutions>
  addManualExecution(id: string, input: ManualExecutionInput, signal?: AbortSignal): Promise<ManualExecutions>
  correctManualExecution(id: string, executionId: string, input: ManualExecutionCorrection, signal?: AbortSignal): Promise<ManualExecutions>
  deletePlay(id: string): Promise<void>
  playExecution(id: string, signal?: AbortSignal): Promise<PlayExecution>
  linkOrder(id: string, link: LinkOrder, signal?: AbortSignal): Promise<PlayExecution>
  unlinkOrder(id: string, linkId: string, signal?: AbortSignal): Promise<PlayExecution>
  /** Moves a linked stop or target to its observed order's price as one plan revision; returns the saved Play. */
  rebindLevel(id: string, linkId: string, request: RebindLevel, signal?: AbortSignal): Promise<SavedPlay>
  evidence(playId: string, signal?: AbortSignal): Promise<SavedEvidence[]>
  evidenceImage(id: string, signal?: AbortSignal): Promise<Blob>
  uploadEvidence(playId: string, image: Blob, fields: { source: 'capture' | 'upload'; note: string; markup: ImageMarkup | null; name: string }): Promise<SavedEvidence>
  updateEvidenceNote(id: string, note: string): Promise<SavedEvidence>
  updateEvidenceMarkup(id: string, markup: ImageMarkup | null): Promise<SavedEvidence>
  deleteEvidence(id: string): Promise<void>
  /** The owner's risk setting, closed-play record and suggestion limits. */
  sizing(signal?: AbortSignal): Promise<SizingDocument>
  /** Saves the risk per trade (0.1 to 5 percent, a decimal string) and returns the new sizing document. */
  updateSizingSettings(settings: { riskPercent: string }): Promise<SizingDocument>
}

const optionalTradingMode = (v: Record<string, unknown>) => v.tradingMode === undefined || v.tradingMode === 'real' || v.tradingMode === 'paper'
const coverages = ['complete', 'partial', 'unavailable']
const validExpected = (ids: string[] | undefined, expected: AccountSettingsExpectation[] | undefined) =>
  expected === undefined || Array.isArray(expected) && !!ids?.length && expected.length === ids.length &&
  expected.every(e => isRecord(e) && isGuid(e.accountId) && ids.includes(e.accountId) && isPositiveCount(e.settingsRevision)) &&
  new Set(expected.map(e => e.accountId)).size === expected.length
const instrumentCatalog = (v: unknown): v is InstrumentCatalog => isRecord(v) && isFilledText(v.venueId, 64) &&
  v.marketScope === 'perpetuals' && ['venue-perpetuals', 'manual'].includes(String(v.scope)) &&
  Array.isArray(v.instruments) && v.instruments.length <= 10_000 &&
  v.instruments.every((i: unknown) => isRecord(i) && isFilledText(i.contractId, 128) && i.contractId.trim() === i.contractId &&
    isCount(i.quantityDecimals) && i.quantityDecimals <= 28 && isPositiveCount(i.maxLeverage) &&
    isText(i.quoteAsset) && /^[A-Za-z0-9]{1,16}$/.test(i.quoteAsset) &&
    (i.priceStep == null || typeof i.priceStep === 'number' && Number.isFinite(i.priceStep) && i.priceStep > 0) &&
    (i.category == null || isText(i.category, 32)) &&
    (i.venueContractId == null || isText(i.venueContractId, 128)) &&
    (i.maintenanceMarginFraction == null || typeof i.maintenanceMarginFraction === 'number' && i.maintenanceMarginFraction > 0 &&
      i.maintenanceMarginFraction < 1)) &&
  new Set(v.instruments.map((i: VenueInstrument) => i.contractId)).size === v.instruments.length &&
  (v.scope !== 'manual' || v.instruments.length === 0) && isText(v.notice, 1000)
const candle = (v: unknown): v is VenueCandle => isRecord(v) && isPositiveCount(v.openTime) && isPositiveCount(v.closeTime) &&
  v.closeTime >= v.openTime && ['open', 'high', 'low', 'close', 'volume'].every(k => isNonNegativeDecimal(v[k])) &&
  isNullOr(v.trades, isCount)
const candleSeries = (query: CandleQuery) => (v: unknown): v is CandleSeries => isRecord(v) && isFilledText(v.venueId, 64) &&
  v.instrument === query.instrument && v.interval === query.interval && v.priceSource === 'trades' &&
  Array.isArray(v.candles) && v.candles.length <= 5_000 && v.candles.every(candle) &&
  v.candles.every((c: VenueCandle, i: number, all: VenueCandle[]) => i === 0 || c.openTime > all[i - 1]!.openTime) &&
  isCount(v.requestedFrom) && isPositiveCount(v.requestedTo) && v.requestedTo >= v.requestedFrom && isTimestamp(v.retrievedAt) &&
  typeof v.historyExhausted === 'boolean' && isText(v.notice, 1000)
const marketContext = (instrument: string) => (v: unknown): v is MarketContext => isRecord(v) &&
  isFilledText(v.venueId, 64) && v.instrument === instrument &&
  ['markPrice', 'oraclePrice', 'dayNotionalVolume', 'openInterest'].every(k => isNonNegativeDecimal(v[k])) &&
  isNullOr(v.previousDayPrice, isNonNegativeDecimal) && isNullOr(v.midPrice, isNonNegativeDecimal) &&
  isPlainDecimal(v.fundingRate) && isNullOr(v.premium, isPlainDecimal) && isTimestamp(v.observedAt) && isText(v.notice, 1000)
const streamStatus = (v: unknown): v is MarketStreamStatus => isRecord(v) &&
  ['live', 'reconnecting', 'stale'].includes(String(v.state)) && isTimestamp(v.observedAt, 40)
const streamLimit = 1024 * 1024
const instrumentPattern = /^[A-Za-z0-9_-]{1,32}$/
const portfolio = (v: unknown): v is Portfolio => isRecord(v) && optionalTradingMode(v) && isGuid(v.id) && isText(v.name) &&
  isCount(v.accountCount) && isNullOr(v.totalValueUsd, isPlainDecimal) &&
  coverages.includes(String(v.valueCoverage)) &&
  (v.balanceUsd === undefined || isNullOr(v.balanceUsd, isPlainDecimal)) &&
  (v.balanceCoverage === undefined || coverages.includes(String(v.balanceCoverage))) &&
  (v.availableBalanceUsd === undefined || isNullOr(v.availableBalanceUsd, isPlainDecimal)) &&
  (v.availableBalanceAccountCount === undefined || isCount(v.availableBalanceAccountCount)) &&
  (v.enabledAccountCount === undefined || isCount(v.enabledAccountCount)) &&
  (v.availableBalanceCoverage === undefined || isText(v.availableBalanceCoverage) && coverages.includes(v.availableBalanceCoverage)) &&
  (v.icon === undefined || isText(v.icon, 40))
const account = (v: unknown): v is BrokerAccount => isRecord(v) && optionalTradingMode(v) &&
  (v.startingCapitalUsd === undefined || isNullOr(v.startingCapitalUsd, isNonNegativeDecimal)) && isGuid(v.id) &&
  isNullOr(v.portfolioId, isGuid) && isText(v.name) && isText(v.venueId) &&
  isNullOr(v.address, isText) && isNullOr(v.accountValueUsd, isPlainDecimal) &&
  (v.sourceId === undefined || isNullOr(v.sourceId, isText)) &&
  isNullOr(v.lastSyncedAtUtc, isTimestamp) &&
  (v.tradingMode === 'paper' ? v.syncStatus === 'paper' : ['manual', 'not-synced', 'synced', 'error'].includes(String(v.syncStatus))) &&
  isNullOr(v.lastSyncError, isText) && isCount(v.positionCount) && isNullOr(v.historyNotice, isText) &&
  (v.isEnabled === undefined || typeof v.isEnabled === 'boolean') &&
  (v.availableStablecoinNominalUsd === undefined || isNullOr(v.availableStablecoinNominalUsd, isPlainDecimal)) &&
  (v.stablecoinScope === undefined || isNullOr(v.stablecoinScope, isText)) &&
  (v.accountMode === undefined || isNullOr(v.accountMode, isText)) &&
  (v.totalStablecoinNominalUsd === undefined || isNullOr(v.totalStablecoinNominalUsd, isPlainDecimal)) &&
  (v.balanceUsd === undefined || isNullOr(v.balanceUsd, isPlainDecimal)) &&
  (v.availableBalanceUsd === undefined || isNullOr(v.availableBalanceUsd, isPlainDecimal)) &&
  (v.availableBalanceSource === undefined || v.availableBalanceSource === null ||
    isText(v.availableBalanceSource) && ['wallet', 'margin', 'withdrawable', 'paper'].includes(v.availableBalanceSource)) &&
  (v.settingsRevision === undefined || isPositiveCount(v.settingsRevision))
const discovery = (venueId: string, address: string) => (v: unknown): v is AccountDiscovery => isRecord(v) &&
  v.venueId === venueId && isText(v.address) && v.address.toLowerCase() === address.toLowerCase() &&
  isText(v.notice, 2000) && Array.isArray(v.accounts) && v.accounts.length <= 1000 &&
  v.accounts.every((a: unknown) => isRecord(a) && accountIndex(a.sourceId) && isText(a.name, 200) &&
    ['main', 'subaccount'].includes(String(a.accountType)) && isNullOr(a.accountValueUsd, isPlainDecimal) &&
    (a.collateralUsd === undefined || isNullOr(a.collateralUsd, isPlainDecimal)) &&
    (a.availableBalanceUsd === undefined || isNullOr(a.availableBalanceUsd, isPlainDecimal)) &&
    isNullOr(a.existingAccountId, isGuid) && (a.isEnabled === null || typeof a.isEnabled === 'boolean')) &&
  new Set(v.accounts.map(a => a.sourceId)).size === v.accounts.length
const credential = (v: unknown): v is AccountCredential => isRecord(v) && typeof v.storageConfigured === 'boolean' &&
  Object.keys(v).every(k => ['storageConfigured', 'credential'].includes(k)) &&
  (v.credential === null || isRecord(v.credential) &&
    Object.keys(v.credential).every(k => ['scope', 'expiresAt', 'lastVerifiedAt', 'status'].includes(k)) &&
    ['single', 'all'].includes(String(v.credential.scope)) && isTimestamp(v.credential.expiresAt) &&
    isNullOr(v.credential.lastVerifiedAt, isTimestamp) &&
    ['valid', 'expiring', 'expired', 'unavailable'].includes(String(v.credential.status)))
const wallet = (v: unknown): v is StablecoinWallet | null => v === null || isRecord(v) &&
  isTimestamp(v.observedAtUtc) && isText(v.accountMode) && isText(v.scope) && isPlainDecimal(v.totalNominalUsd) &&
  isPlainDecimal(v.availableNominalUsd) && isText(v.notice) && Array.isArray(v.balances) &&
  v.balances.every(b => isRecord(b) && isText(b.symbol) && isCount(b.tokenIndex) && isText(b.tokenId) &&
    isPlainDecimal(b.total) && isPlainDecimal(b.held) && isPlainDecimal(b.available))
const snapshot = (v: unknown): v is AccountSnapshot | null => v === null || isRecord(v) &&
  isTimestamp(v.observedAtUtc) && isText(v.valueScope) &&
  ['accountValueUsd', 'withdrawableUsd', 'marginUsedUsd'].every(k => isNullOr(v[k], isPlainDecimal)) &&
  (v.availableMarginUsd === undefined || isNullOr(v.availableMarginUsd, isPlainDecimal)) &&
  Array.isArray(v.positions) && v.positions.every(p => isRecord(p) && isText(p.contractId) &&
    ['signedQuantity', 'entryPrice', 'unrealizedPnlUsd', 'marginUsedUsd'].every(k => isPlainDecimal(p[k])) &&
    (p.leverage === null || typeof p.leverage === 'number' && Number.isInteger(p.leverage) && p.leverage > 0)) &&
  (v.stablecoinWallet === undefined || wallet(v.stablecoinWallet))
const overview = (v: unknown): v is Overview => isRecord(v) && Array.isArray(v.portfolios) && v.portfolios.every(portfolio) &&
  Array.isArray(v.accounts) && v.accounts.every(account) && isRecord(v.totals) &&
  ['portfolioCount', 'accountCount', 'valuedAccountCount', 'openPositionCount', 'importedFillCount'].every(k => isRecord(v.totals) && isCount(v.totals[k])) &&
  isNullOr(v.totals.totalAccountValueUsd, isPlainDecimal) &&
  (v.totals.availableStablecoinNominalUsd === undefined || isNullOr(v.totals.availableStablecoinNominalUsd, isPlainDecimal)) &&
  (v.totals.stablecoinAccountCount === undefined || isCount(v.totals.stablecoinAccountCount)) &&
  (v.totals.totalBalanceUsd === undefined || isNullOr(v.totals.totalBalanceUsd, isPlainDecimal)) &&
  (v.totals.availableBalanceUsd === undefined || isNullOr(v.totals.availableBalanceUsd, isPlainDecimal)) &&
  ['balanceAccountCount', 'availableBalanceAccountCount', 'enabledAccountCount'].every(k => isRecord(v.totals) &&
    (v.totals[k] === undefined || isCount(v.totals[k]))) &&
  ['balanceCoverage', 'availableBalanceCoverage'].every(k => isRecord(v.totals) &&
    (v.totals[k] === undefined || coverages.includes(String(v.totals[k])))) &&
  Array.isArray(v.recentActivity) && v.recentActivity.every(isImportedFill) && isText(v.scopeNote)

function assertResponseMode(value: unknown, mode: TradingMode, requireMode = false): void {
  if (Array.isArray(value)) { value.forEach(item => assertResponseMode(item, mode)); return }
  if (!isRecord(value)) return
  const entity = typeof value.id === 'string' && ('accountValueUsd' in value && 'venueId' in value || 'planRevision' in value && 'accountId' in value || 'valueCoverage' in value && 'accountCount' in value)
  if ((requireMode || entity || value.tradingMode !== undefined) && (value.tradingMode ?? 'real') !== mode) {
    throw new ApiError('invalid-response', 'The API returned data for a different trading mode. Reload this workspace.')
  }
  for (const key of ['summary', 'play', 'accounts', 'portfolios', 'plays', 'items']) {
    if (value[key] !== undefined) assertResponseMode(value[key], mode)
  }
}

/**
 * A 401 on a session call ends the session. A 403 never does, except `user-suspended`; other refusals only explain
 * themselves. Token mode keeps treating both as a rejected token.
 */
export function endsSession(credentials: string | ApiCredentials, failure: AuthFailure) {
  return typeof credentials === 'string' || credentials.kind === 'bearer' || failure.status === 401 || failure.code === 'user-suspended'
}

/** `onAuthFailure` hears every 401 and 403 of this client, so the session owner can tear down or open a dialog. */
export function createWorkspaceApi(credentials: string | ApiCredentials, onAuthFailure?: (failure: AuthFailure) => void, tradingMode?: TradingMode): WorkspaceApi {
  const rawCall = createApiCall(credentials, failure => {
    if (endsSession(credentials, failure)) emitApiEvent(api, { type: 'unauthorized' })
    onAuthFailure?.(failure)
  }, () => emitApiEvent(api, { type: 'session-changed' }))
  // Bind a mode to the client, not mutable global state: late responses can only return to their original shell.
  const call: typeof rawCall = async (path, options) => {
    if (tradingMode === 'paper' && /^\/api\/(?:accounts\/[^/?]+\/(?:sync|fills|credential|monitor|discrepancies|market-context|market-stream|stream)|plays\/[^/?]+\/(?:manual-executions|execution|discrepancies)|plays\/retrospective|monitor|events|venues)(?:[/?]|$)/.test(path)) {
      throw new ApiError('unavailable', 'This real-account feature is unavailable in Paper mode.')
    }
    const scoped = /^\/api\/(?:overview|accounts|portfolios|plays|activity|review|sizing|evidence)(?:[/?]|$)/.test(path)
    const url = new URL(path, 'http://vessel.local')
    if (scoped && tradingMode) url.searchParams.set('tradingMode', tradingMode)
    const value = await rawCall(`${url.pathname}${url.search}`, options)
    // Mode-bearing report roots must explicitly identify Paper; omitted modes remain legacy Real.
    const report = ['/api/review', '/api/sizing', '/api/activity', '/api/overview/summary'].includes(url.pathname)
    if (scoped) assertResponseMode(value, tradingMode ?? 'real', report)
    return value
  }
  const generation = typeof credentials !== 'string' && credentials.kind === 'session' ? credentials.generation : undefined
  /**
   * Runs one stream under the current session generation. A newer generation aborts it, so no frame read under the
   * old one reaches the caller, and opens it again under the new one; `open` reads only its own signal. The end of the
   * session aborts it for good: it settles quietly and never reopens, even while its header read, a session call that
   * then never settles, is still pending.
   */
  async function generationBound(signal: AbortSignal, open: (signal: AbortSignal) => Promise<void>) {
    if (!generation) return open(signal)
    const { ended } = generation
    while (!signal.aborted && !ended.aborted) {
      const changed = new AbortController()
      const unsubscribe = generation.subscribe(() => changed.abort())
      let quit!: () => void
      const over = new Promise<void>(resolve => { quit = resolve })
      ended.addEventListener('abort', quit, { once: true })
      try {
        const opened = open(AbortSignal.any([signal, changed.signal, ended]))
        // The session may end before the reader rejects; still observe that later rejection.
        opened.catch(() => {})
        await Promise.race([opened, over])
      } catch (cause) {
        if (signal.aborted || !changed.signal.aborted && !ended.aborted) throw cause
      } finally {
        ended.removeEventListener('abort', quit)
        unsubscribe()
      }
      if (!changed.signal.aborted) return
    }
  }
  // Session refusals use Vessel's copy for their code; other statuses keep each endpoint's own messages.
  const codeMessage = (code: string, status: number) => status === 401 || status === 403 ? authErrorMessage(code) : undefined
  const workspaceRequest = { timeout: 10_000, notFound: 'This account or portfolio is no longer available.', codeMessage,
    noContent: 'The API returned no workspace data.', invalidData: 'The API returned incompatible workspace data.' }
  const playRequest = { timeout: 15_000, notFound: 'This play or image is no longer available.', codeMessage,
    noContent: 'The API returned no play data.', invalidData: 'The API returned incompatible play data.' }
  const resourceId = (id: string) => {
    if (!isGuid(id)) throw new ApiError('invalid-response', 'The account identifier is invalid.')
    return id
  }
  const accountPath = (id: string) => `/api/accounts/${resourceId(id)}`
  async function marketStream(id: string, query: MarketStreamQuery, signal: AbortSignal, onEvent: (event: MarketStreamEvent) => void) {
    if (!instrumentPattern.test(query.instrument) || !candleIntervals.includes(query.interval)) {
      throw new ApiError('invalid-response', 'The live chart request is invalid.')
    }
    const path = `${accountPath(id)}/market-stream?${new URLSearchParams({ instrument: query.instrument, interval: query.interval })}`
    await generationBound(signal, bound => readMarketStream(path, query, bound, onEvent))
  }
  async function readMarketStream(path: string, query: MarketStreamQuery, signal: AbortSignal, onEvent: (event: MarketStreamEvent) => void) {
    const interrupted = () => new ApiError('unavailable', 'The live market stream was interrupted.')
    // The pipeline bounds the header read, not the lifetime of the stream.
    let response: Response
    try {
      response = await call(path, { ...workspaceRequest, ...withSignal(signal), timeout: 25_000, accept: 'text/event-stream',
        unavailable: 'The live market stream did not connect. Check the API and try again.',
        badGateway: 'Venue market data is unavailable. Try refreshing the chart.',
        tooMany: 'Too many live chart streams. Close another chart and try again.',
        read: async response => response })
    } catch (cause) {
      if (signal.aborted) return
      throw cause
    }
    if (!response.body || !/^text\/event-stream\b/i.test(response.headers.get('Content-Type') ?? '')) {
      await response.body?.cancel().catch(() => undefined)
      throw new ApiError('invalid-response', 'The API returned an invalid live market stream.')
    }
    const isContext = marketContext(query.instrument)
    const parser = new SseParser(({ event, data }) => {
      if (signal.aborted || data.length > streamLimit) return
      let body: unknown
      try { body = JSON.parse(data) } catch { return }
      if (event === 'candle' && candle(body)) onEvent({ type: 'candle', candle: body })
      else if (event === 'context' && isContext(body)) onEvent({ type: 'context', context: body })
      else if (event === 'status' && streamStatus(body)) onEvent({ type: 'status', status: body })
    }, streamLimit)
    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    const stop = () => { void reader.cancel().catch(() => undefined) }
    signal.addEventListener('abort', stop, { once: true })
    try {
      for (;;) {
        let chunk: ReadableStreamReadResult<Uint8Array>
        try { chunk = await reader.read() } catch {
          if (signal.aborted) return
          throw interrupted()
        }
        if (signal.aborted) return
        if (chunk.done) {
          parser.push(decoder.decode())
          parser.end()
          return
        }
        try { parser.push(decoder.decode(chunk.value, { stream: true })) } catch (error) {
          if (error instanceof SseOverflowError) {
            stop()
            throw new ApiError('invalid-response', 'The live market stream sent an oversized message.')
          }
          throw error
        }
      }
    } finally {
      signal.removeEventListener('abort', stop)
    }
  }
  const playPath = (id: string) => {
    if (!isGuid(id)) throw new ApiError('invalid-response', 'The play identifier is invalid.')
    return `/api/plays/${id}`
  }
  const evidencePath = (id: string) => {
    if (!isGuid(id)) throw new ApiError('invalid-response', 'The image identifier is invalid.')
    return `/api/evidence/${id}`
  }
  const none = (v: unknown): v is undefined => v === undefined
  const notificationRequest = { timeout: 10_000, notFound: 'This notification is no longer available.', codeMessage,
    unavailable: 'Notifications are unavailable. Check the API and try again.', invalidData: 'The API returned incompatible notification data.' }
  // A conflict asks the caller to reload, so it gets its own fixed text and stays distinguishable by status.
  const conflict = <T,>(request: Promise<T>, message: string) => request.catch((cause: unknown) => {
    throw cause instanceof ApiError && cause.status === 409 ? new ApiError('http', message, 409) : cause
  })
  const withSignal = (signal?: AbortSignal) => signal ? { signal } : {}
  const api: WorkspaceApi = {
    tradingMode: tradingMode ?? 'real',
    forTradingMode: mode => createWorkspaceApi(credentials, onAuthFailure, mode),
    paperSettings: signal => call('/api/paper/settings', { ...workspaceRequest, validate: isPaperSettings, ...withSignal(signal) }),
    savePaperSettings: settings => {
      if (!isPaperSettings(settings)) return Promise.reject(new ApiError('invalid-response', 'Use a whole paper check interval from 1 to 1440 minutes.'))
      return call('/api/paper/settings', { ...workspaceRequest, validate: isPaperSettings, method: 'PUT', body: JSON.stringify(settings) })
    },
    paperState: (id, signal) => {
      if (tradingMode !== 'paper') return Promise.reject(new ApiError('unavailable', 'Simulated executions are only available in Paper mode.'))
      return call(`${playPath(id)}/paper-state`, { ...playRequest, validate: (v): v is PaperPlayState => isPaperPlayState(v) && v.playId === id, ...withSignal(signal) })
    },
    monitor: signal => call('/api/monitor', { ...workspaceRequest, validate: isMonitorSummary, ...withSignal(signal) }),
    accountMonitor: (id, signal) => call(`${accountPath(id)}/monitor`, { ...workspaceRequest,
      validate: (v): v is AccountMonitor => isAccountMonitor(v) && v.accountId === id, ...withSignal(signal) }),
    runAccountMonitor: (id, signal) => call(`${accountPath(id)}/monitor/run`, { ...workspaceRequest,
      validate: (v): v is MonitorRunAccepted => isMonitorRunAccepted(v) && v.monitor.accountId === id, method: 'POST', ...withSignal(signal),
      tooMany: 'A check is already running for this account or it was checked moments ago. Try again when the countdown ends.',
      serviceUnavailable: 'The monitor is offline, so the check could not start. Try again once the monitor is back.' }),
    monitorEvents: (after, limit, signal) => {
      if (!isRequestCursor(after) || !Number.isInteger(limit) || limit < 1 || limit > 200) {
        return Promise.reject(new ApiError('invalid-response', 'The event cursor is invalid.'))
      }
      return call(`/api/events?${new URLSearchParams({ after, limit: String(limit) })}`, { ...workspaceRequest,
        validate: isMonitorEventPage, unavailable: 'Monitor events are unavailable.', ...withSignal(signal) })
    },
    monitorStream: async (after, signal, onEvent, notificationsAfter) => {
      if (!isRequestCursor(after)) throw new ApiError('invalid-response', 'The event cursor is invalid.')
      if (notificationsAfter !== undefined && !isNotificationCursor(notificationsAfter)) throw new ApiError('invalid-response', 'The notification cursor is invalid.')
      // A stream reopened for a newer session generation resumes where the caller's cursor would.
      let resume = after
      let notificationResume = notificationsAfter
      await generationBound(signal, async bound => {
        if (bound.aborted) return
        let response: Response
        try {
          response = await call(`/api/events/stream?${new URLSearchParams({ after: resume, ...(notificationResume !== undefined ? { notificationsAfter: notificationResume } : {}) })}`, {
            ...workspaceRequest, signal: bound, timeout: 25_000, accept: 'text/event-stream',
            unavailable: 'The monitor event stream did not connect.', read: async response => response,
          })
        } catch (cause) {
          if (bound.aborted) return
          throw cause
        }
        if (!response.body || !/^text\/event-stream\b/i.test(response.headers.get('Content-Type') ?? '')) {
          await response.body?.cancel().catch(() => undefined)
          throw new ApiError('invalid-response', 'The API returned an invalid monitor stream.')
        }
        try {
          await readSseStream(response.body, bound, ({ event, data, id }) => {
            const frame = monitorFrame(event, data, id)
            if (!frame || bound.aborted) return
            if (frame.type === 'event') { if (isAfter(frame.cursor, resume)) resume = frame.cursor }
            else if (frame.type === 'status' && (isUnknownCursor(resume) || frame.status.state === 'reset')) resume = frame.status.nextCursor
            else if (frame.type === 'notifications' && notificationResume !== undefined && frame.status.state === 'reset') notificationResume = frame.status.nextCursor
            else if (frame.type === 'notification' && notificationResume !== undefined && isNotificationAfter(frame.frame.cursor, notificationResume)) notificationResume = frame.frame.cursor
            onEvent(frame)
          })
        } catch {
          if (!bound.aborted) throw new ApiError('unavailable', 'The monitor event stream was interrupted.')
        }
      })
    },
    notificationHead: signal => call('/api/notifications/head', { ...notificationRequest, validate: isNotificationHead, ...withSignal(signal) }),
    notifications: (query, signal) => {
      let path: string
      try { path = notificationPagePath(query) } catch (cause) { return Promise.reject(cause) }
      return call(path, { ...notificationRequest, validate: (v): v is NotificationPage => isNotificationPage(v, query), ...withSignal(signal) })
    },
    claimNotifications: signal => call('/api/notifications/claim', { ...notificationRequest, validate: isNotificationClaim, method: 'POST', ...withSignal(signal) }),
    readNotification: (id, signal) => {
      if (!isGuid(id)) return Promise.reject(new ApiError('invalid-response', 'The notification identifier is invalid.'))
      return call(`/api/notifications/${id}/read`, { ...notificationRequest, method: 'POST', ...withSignal(signal),
        validate: (v): v is InAppNotification => isInAppNotification(v) && v.id.toLowerCase() === id.toLowerCase() })
    },
    readAllNotifications: async (upTo, signal) => {
      if (!isNotificationCursor(upTo)) throw new ApiError('invalid-response', 'The notification cursor is invalid.')
      return conflict(call('/api/notifications/read', { ...notificationRequest, validate: isNotificationReadAll, method: 'POST',
        body: JSON.stringify({ upTo }), ...withSignal(signal) }), notificationReadAllConflict)
    },
    notificationPreferences: signal => call('/api/notifications/preferences', { ...notificationRequest, validate: isNotificationPreferences, ...withSignal(signal) }),
    saveNotificationPreferences: async ({ enabled, minimumSeverity, categories, expectedRevision }) => {
      if (!isNotificationPreferenceFields({ enabled, minimumSeverity, categories }) || !isCount(expectedRevision)) {
        throw new ApiError('invalid-response', 'The notification preferences are invalid.')
      }
      return conflict(call('/api/notifications/preferences', { ...notificationRequest, validate: isNotificationPreferences, method: 'PUT',
        body: JSON.stringify({ enabled, minimumSeverity, categories, expectedRevision }) }), notificationPreferencesConflict)
    },
    requestTestNotification: () => call('/api/notifications/test', { ...notificationRequest, method: 'POST', read: async () => undefined,
      tooMany: 'A test notification was requested moments ago. Wait for the countdown, then try again.' }),
    dismissPlayDiscrepancy: (id, discrepancyId, note, signal) => call(`${playPath(id)}/discrepancies/${resourceId(discrepancyId)}/dismiss`,
      { ...playRequest, validate: none, method: 'POST', body: JSON.stringify({ note: note ?? null }), ...withSignal(signal) }),
    dismissAccountDiscrepancy: (id, discrepancyId, note, signal) => call(`${accountPath(id)}/discrepancies/${resourceId(discrepancyId)}/dismiss`,
      { ...workspaceRequest, validate: none, method: 'POST', body: JSON.stringify({ note: note ?? null }), ...withSignal(signal) }),
    plays: (query, signal) => call(playListPath(query), { ...playRequest, validate: (v): v is PlayList => isPlayList(v, query), ...withSignal(signal) }),
    playCounts: (query, signal) => call(playCountsPath(query), { ...playRequest, validate: isPlayCounts, ...withSignal(signal) }),
    play: (id, signal) => call(playPath(id), { ...playRequest, validate: isSavedPlay, ...withSignal(signal) }),
    createPlay: fields => call('/api/plays', { ...playRequest, validate: isSavedPlay, method: 'POST', body: JSON.stringify(fields) }),
    updatePlay: (id, fields) => call(playPath(id), { ...playRequest, validate: isSavedPlay, method: 'PUT', body: JSON.stringify(fields) }),
    changePlayStatus: (id, expectedVersion, request) =>
      call(`${playPath(id)}/status`, { ...playRequest, validate: isSavedPlay, method: 'POST', body: JSON.stringify({ expectedVersion, ...request }) }),
    playHistory: (id, signal) => call(`${playPath(id)}/history`, { ...playRequest,
      validate: (v): v is PlayHistory => isPlayHistory(v) && v.statusChanges.every(change => tradingMode === 'paper'
        ? change.source === undefined || change.source === 'owner' || change.source === 'paper' : change.source !== 'paper'),
      ...withSignal(signal) }),
    completeReview: (id, expectedVersion) => call(`${playPath(id)}/review/complete`,
      { ...playRequest, validate: isSavedPlay, method: 'POST', body: JSON.stringify({ expectedVersion }) }),
    reopenReview: (id, expectedVersion) => call(`${playPath(id)}/review/reopen`,
      { ...playRequest, validate: isSavedPlay, method: 'POST', body: JSON.stringify({ expectedVersion }) }),
    noReviewNeeded: (id, expectedVersion) => call(`${playPath(id)}/review/not-needed`,
      { ...playRequest, validate: isSavedPlay, method: 'POST', body: JSON.stringify({ expectedVersion }) }),
    declareUnresolved: (id, expectedVersion, reason, note) => call(`${playPath(id)}/unresolved`,
      { ...playRequest, validate: isSavedPlay, method: 'POST', body: JSON.stringify({ expectedVersion, reason, note }) }),
    returnToOpen: (id, expectedVersion, note) => call(`${playPath(id)}/return-to-open`,
      { ...playRequest, validate: isSavedPlay, method: 'POST', body: JSON.stringify({ expectedVersion, note }) }),
    playNotes: (id, signal) => call(`${playPath(id)}/notes`, { ...playRequest,
      validate: (v): v is PlayNote[] => Array.isArray(v) && v.length <= 500 && v.every(isPlayNote), ...withSignal(signal) }),
    addPlayNote: (id, request) => call(`${playPath(id)}/notes`, { ...playRequest, method: 'POST', body: JSON.stringify(request),
      validate: (v): v is PlayNote[] => Array.isArray(v) && v.length <= 500 && v.every(isPlayNote) }),
    nextActions: signal => call('/api/plays/next-actions', { ...playRequest,
      validate: (v): v is NextAction[] => Array.isArray(v) && v.length <= 10 && v.every(isNextAction), ...withSignal(signal) }),
    manualExecutions: (id, signal) => call(`${playPath(id)}/manual-executions`, { ...playRequest, validate: (v): v is ManualExecutions => isManualExecutions(v) && v.play.summary.id === id, ...withSignal(signal) }),
    addManualExecution: (id, input, signal) => {
      if (!isManualExecutionInput(input)) return Promise.reject(new ApiError('invalid-response', 'Provide valid manual execution facts, a UTC time and a request identity.'))
      return call(`${playPath(id)}/manual-executions`, { ...playRequest, validate: (v): v is ManualExecutions => isManualExecutions(v) && v.play.summary.id === id, method: 'POST', body: JSON.stringify(input), ...withSignal(signal) })
    },
    correctManualExecution: (id, executionId, input, signal) => {
      if (!isManualExecutionCorrection(input)) return Promise.reject(new ApiError('invalid-response', 'Provide valid correction facts and a reason.'))
      return call(`${playPath(id)}/manual-executions/${resourceId(executionId)}`, { ...playRequest, validate: (v): v is ManualExecutions => isManualExecutions(v) && v.play.summary.id === id, method: 'PUT', body: JSON.stringify(input), ...withSignal(signal) })
    },
    deletePlay: id => call(playPath(id), { ...playRequest, validate: none, method: 'DELETE' }),
    playExecution: (id, signal) => call(`${playPath(id)}/execution`, { ...playRequest, validate: isPlayExecution, ...withSignal(signal) }),
    linkOrder: (id, link, signal) => call(`${playPath(id)}/execution/links`, { ...playRequest, validate: isPlayExecution, method: 'POST', body: JSON.stringify(link), ...withSignal(signal) }),
    unlinkOrder: (id, linkId, signal) => {
      if (!isGuid(linkId)) return Promise.reject(new ApiError('invalid-response', 'The link identifier is invalid.'))
      return call(`${playPath(id)}/execution/links/${linkId}`, { ...playRequest, validate: isPlayExecution, method: 'DELETE', ...withSignal(signal) })
    },
    rebindLevel: (id, linkId, request, signal) => {
      if (!isGuid(linkId)) return Promise.reject(new ApiError('invalid-response', 'The link identifier is invalid.'))
      return call(`${playPath(id)}/execution/links/${linkId}/rebind`, { ...playRequest,
        validate: (v): v is SavedPlay => isSavedPlay(v) && v.summary.id === id, method: 'POST', body: JSON.stringify(request), ...withSignal(signal) })
    },
    evidence: (playId, signal) => call(`${playPath(playId)}/evidence`, { ...playRequest, validate: (v): v is SavedEvidence[] => Array.isArray(v) && v.every(isSavedEvidence), ...withSignal(signal) }),
    evidenceImage: (id, signal) => call(`${evidencePath(id)}/content`, { ...playRequest, ...withSignal(signal),
      timeout: 60_000, accept: 'image/*', read: response => response.blob() }),
    uploadEvidence: (playId, image, fields) => {
      const form = new FormData()
      form.append('file', image, fields.name)
      form.append('source', fields.source)
      form.append('note', fields.note)
      if (fields.markup) form.append('markup', JSON.stringify(fields.markup))
      return call(`${playPath(playId)}/evidence`, { ...playRequest, validate: isSavedEvidence, method: 'POST', body: form, timeout: 60_000 })
    },
    updateEvidenceNote: (id, note) => call(evidencePath(id), { ...playRequest, validate: isSavedEvidence, method: 'PATCH', body: JSON.stringify({ note }) }),
    updateEvidenceMarkup: (id, markup) => call(`${evidencePath(id)}/markup`, { ...playRequest, validate: isSavedEvidence, ...(markup ? { method: 'PUT', body: JSON.stringify(markup) } : { method: 'DELETE' }) }),
    deleteEvidence: id => call(evidencePath(id), { ...playRequest, validate: none, method: 'DELETE' }),
    sizing: signal => call('/api/sizing', { ...playRequest, validate: isSizingDocument, ...withSignal(signal) }),
    review: (query, signal) => call(reviewPath(query), { ...playRequest, validate: isReviewDocument, ...withSignal(signal) }),
    reviewExclusions: (query, signal) => call(reviewExclusionsPath(query), { ...playRequest, validate: isReviewExclusions, ...withSignal(signal) }),
    retrospectivePreview: (accountId, operationId, signal) => call(retrospectivePreviewPath(resourceId(accountId), operationId), { ...playRequest, validate: (v): v is RetrospectivePreview => isRetrospectivePreview(v) && v.operation.accountId === accountId && v.operation.id === operationId, ...withSignal(signal) }),
    createRetrospectivePlay: (input, signal) => {
      if (!isCreateRetrospectivePlayRequest(input)) return Promise.reject(new ApiError('invalid-response', 'Provide a valid retrospective request and preview fingerprint.'))
      return call(retrospectivePlayPath(), { ...playRequest, validate: (v): v is SavedPlay => isSavedPlay(v) &&
        v.summary.accountId === input.accountId && v.summary.origin === 'retrospective', method: 'POST', body: JSON.stringify(input), ...withSignal(signal) })
    },
    updateSizingSettings: settings => tradingMode === 'paper' ? Promise.reject(new ApiError('http', 'Paper uses the shared risk preference read-only. Edit it in Real mode.', 409)) : call('/api/sizing/settings', { ...playRequest, validate: isSizingDocument, method: 'PUT', body: JSON.stringify({ riskPercent: settings.riskPercent }) }),
    activity: (query, signal) => call(activityPath(query), { ...workspaceRequest, validate: (v): v is ActivityPage => isActivityPage(v, query), ...withSignal(signal) }),
    activityFills: (accountId, operationId, signal) => call(activityFillsPath(accountId, operationId, tradingMode ?? 'real'), { ...workspaceRequest,
      notFound: 'This operation changed. Reload Activity to see its current executions.',
      validate: (v): v is ActivityFills => isActivityFills(v, accountId, operationId), ...withSignal(signal) }),
    overviewSummary: (query, signal) => call(overviewSummaryPath(query), { ...workspaceRequest, validate: (v): v is OverviewSummaryDocument => isOverviewSummary(v, query), ...withSignal(signal) }),
    overview: signal => call('/api/overview', { ...workspaceRequest, validate: overview, ...withSignal(signal) }),
    portfolios: signal => call('/api/portfolios', { ...workspaceRequest, validate: (v): v is Portfolio[] => Array.isArray(v) && v.every(portfolio), ...withSignal(signal) }),
    accounts: signal => call('/api/accounts', { ...workspaceRequest, validate: (v): v is BrokerAccount[] => Array.isArray(v) && v.every(account), ...withSignal(signal) }),
    account: (id, signal) => call(accountPath(id), { ...workspaceRequest, validate: account, ...withSignal(signal) }),
    instruments: (id, signal) => call(`${accountPath(id)}/instruments`, { ...workspaceRequest, validate: instrumentCatalog, ...withSignal(signal), timeout: 25_000, badGateway: 'The venue instrument catalogue is unavailable. Try again or enter a manual label.' }),
    candles: (id, query, signal) => {
      if (!instrumentPattern.test(query.instrument) || !candleIntervals.includes(query.interval) ||
        (query.endTime !== undefined && !isPositiveCount(query.endTime))) {
        return Promise.reject(new ApiError('invalid-response', 'The chart request is invalid.'))
      }
      const params = new URLSearchParams({ instrument: query.instrument, interval: query.interval })
      if (query.endTime !== undefined) params.set('endTime', String(query.endTime))
      if (query.refresh === true) params.set('refresh', 'true')
      return call(`${accountPath(id)}/candles?${params}`, { ...workspaceRequest, validate: candleSeries(query), ...withSignal(signal), timeout: 25_000, badGateway: 'Venue candles are unavailable. Try refreshing the chart.' })
    },
    marketContext: (id, instrument, signal) => {
      if (!instrumentPattern.test(instrument)) return Promise.reject(new ApiError('invalid-response', 'The market request is invalid.'))
      return call(`${accountPath(id)}/market-context?${new URLSearchParams({ instrument })}`, { ...workspaceRequest, validate: marketContext(instrument), ...withSignal(signal), timeout: 25_000, badGateway: 'Venue market statistics are unavailable.' })
    },
    marketStream,
    createPortfolio: (name, icon, accountIds, expected) => {
      if (!validExpected(accountIds, expected)) return Promise.reject(new ApiError('invalid-response', 'Reload accounts and provide one positive settings revision per selected account.'))
      return call('/api/portfolios', { ...workspaceRequest, validate: portfolio, method: 'POST', body: JSON.stringify({ name, icon, accountIds, expected, ...(tradingMode ? { tradingMode } : {}) }) })
    },
    moveAccounts: (accountIds, portfolioId, expected) => {
      if (!accountIds.length || accountIds.length > 200 || !accountIds.every(isGuid) || new Set(accountIds).size !== accountIds.length ||
          portfolioId !== null && !isGuid(portfolioId)) return Promise.reject(new ApiError('invalid-response', 'Choose between 1 and 200 distinct accounts and a valid portfolio.'))
      if (!validExpected(accountIds, expected)) return Promise.reject(new ApiError('invalid-response', 'Reload accounts and provide one positive settings revision per selected account.'))
      return call('/api/accounts/portfolio', { ...workspaceRequest, validate: (v): v is BrokerAccount[] => Array.isArray(v) && v.length === accountIds.length &&
        v.every(a => account(a) && accountIds.includes(a.id) && a.portfolioId === portfolioId && isPositiveCount(a.settingsRevision)) &&
        new Set(v.map(a => a.id)).size === v.length, method: 'POST', body: JSON.stringify({ accountIds, portfolioId, expected }) })
    },
    createAccount: body => {
      if (body.tradingMode !== undefined && body.tradingMode !== (tradingMode ?? 'real')) return Promise.reject(new ApiError('invalid-response', 'The account must match this workspace mode.'))
      if (tradingMode === 'paper' && (!body.portfolioId || !isGuid(body.portfolioId) || !isNonNegativeDecimal(body.startingCapitalUsd) ||
        body.address !== undefined || body.sourceId !== undefined || body.manualAccountValueUsd !== undefined)) {
        return Promise.reject(new ApiError('invalid-response', 'Choose a paper portfolio and valid starting capital. Real account identities are not accepted.'))
      }
      return call('/api/accounts', { ...workspaceRequest, validate: account, method: 'POST', body: JSON.stringify({ ...body, ...(tradingMode ? { tradingMode } : {}) }) })
    },
    discoverAccounts: (venueId, address, signal, token) => {
      if (!/^[a-z][a-z\d-]{0,63}$/.test(venueId) || !/^0x[\da-f]{40}$/i.test(address)) {
        return Promise.reject(new ApiError('invalid-response', 'Use a valid venue and public wallet address.'))
      }
      if (token !== undefined) {
        if (!credentialTransportAllowed()) return Promise.reject(new ApiError('http', credentialTransportMessage))
        if (!token.trim() || token.length > 512) return Promise.reject(new ApiError('http', credentialSaveMessage))
        token = token.trim()
        return call(`/api/venues/${venueId}/accounts/credential`, { ...workspaceRequest, validate: discovery(venueId, address), ...withSignal(signal), method: 'POST', body: JSON.stringify({ address, token }), timeout: 30_000, safeError: 'Could not load venue names. Check the read-only token’s wallet, scope and expiry.' })
      }
      return call(`/api/venues/${venueId}/accounts?${new URLSearchParams({ address })}`, { ...workspaceRequest, validate: discovery(venueId, address), ...withSignal(signal), timeout: 30_000, safeError: 'Could not find accounts for this wallet. Check the address and try again.' })
    },
    accountCredential: (id, signal) => call(`${accountPath(id)}/credential`, { ...workspaceRequest, validate: credential, ...withSignal(signal), timeout: 10_000, safeError: 'Credential status is unavailable. Retry or ask the operator to check credential storage.' }),
    saveAccountCredential: (id, token) => {
      if (!credentialTransportAllowed()) return Promise.reject(new ApiError('http', credentialTransportMessage))
      if (!token.trim() || token.length > 512) return Promise.reject(new ApiError('http', credentialSaveMessage))
      token = token.trim()
      return call(`${accountPath(id)}/credential`, { ...workspaceRequest, validate: credential, method: 'PUT', body: JSON.stringify({ token }), timeout: 30_000, safeError: credentialSaveMessage })
    },
    deleteAccountCredential: id => call(`${accountPath(id)}/credential`, { ...workspaceRequest, validate: none, method: 'DELETE', timeout: 10_000, safeError: 'Could not remove the credential. Refresh its status before trying again.' }),
    updatePortfolio: (id, { name, icon }) => call(`/api/portfolios/${resourceId(id)}`, { ...workspaceRequest, validate: portfolio, method: 'PATCH', body: JSON.stringify({ name, icon }) }),
    deletePortfolio: id => call(`/api/portfolios/${resourceId(id)}`, { ...workspaceRequest, validate: (v): v is undefined => v === undefined, method: 'DELETE' }),
    updateAccount: async (id, settings) => {
      const updated = await call(accountPath(id), { ...workspaceRequest, validate: account, method: 'PUT', body: JSON.stringify(settings) })
      if (updated.isEnabled === false) emitApiEvent(api, { type: 'account-removed', accountId: id })
      return updated
    },
    deleteAccount: async id => {
      await call(accountPath(id), { ...workspaceRequest, validate: (v): v is undefined => v === undefined, method: 'DELETE' })
      emitApiEvent(api, { type: 'account-removed', accountId: id })
    },
    snapshot: (id, signal) => call(`${accountPath(id)}/snapshot`, { ...workspaceRequest, validate: snapshot, ...withSignal(signal) }),
    fills: (id, signal) => call(`${accountPath(id)}/fills`, { ...workspaceRequest, validate: (v): v is ImportedFill[] => Array.isArray(v) && v.every(isImportedFill), ...withSignal(signal) }),
    // History, funding and order retention each allow 20s; leave room for account locking and persistence.
    sync: (id, signal) => call(`${accountPath(id)}/sync`, { ...workspaceRequest, validate: account, method: 'POST', ...withSignal(signal), timeout: 90_000, badGateway: 'The venue refresh failed. Previous account data is still available.' }),
  }
  return api
}
