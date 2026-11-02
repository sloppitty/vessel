import type { PlayFunding } from '@/api/play-funding'
import { InfoTip } from '@/components/ui/info-tip'
import { Time } from '@/components/ui/time'
import { fundingNote } from './LocalResult'

/** What the settled funding line is, for its tip (Phase 2, item 9b). Kept short; venue-funding.md carries the proof. */
export const playFundingNote = 'Funding the venue settled while this play alone held the position: from its first entry fill to its ' +
  'last exit, or to the latest covered read while it holds a position. Positive is received, negative paid; listed stablecoins ' +
  `count 1:1. Shown beside the results and never added to either. ${fundingNote}.`

const labels = { shared: 'Shared, not allocated', incomplete: 'Coverage incomplete', unavailable: 'Not available' } as const

const reasons: Record<Exclude<PlayFunding['reason'], null>, string> = {
  'other-play': 'Another play traded this position during the holding window.',
  'unlinked-fill': 'Fills linked to no play traded this position during the holding window.',
  'open-position': 'The position was already open when this play’s first entry filled.',
  allocated: 'A fill of this position is split between plays, so this play did not hold it alone.',
  identity: 'The position’s market cannot be told apart from another market of this instrument.',
  'missing-executions': 'An order of this play has executions that are not retained.',
  'not-settled': 'This play’s own fills do not settle it.',
  'omitted-executions': 'The venue reported executions that were left out of this position during the holding window.',
  'history-gap': 'Execution history has an open gap during the holding window.',
  'history-unproven': 'Execution history is not proven continuous over the holding window.',
  'flat-start-unproven': 'Nothing on record proves the position was flat before the first entry.',
  'flat-end-unproven': 'Nothing on record proves the position was flat after the last exit.',
  'operation-boundaries': 'The position’s retained fills do not bound this play’s holding window.',
  'funding-coverage': 'Settled funding has not been read for the whole holding window.',
  'funding-ambiguous': 'A settlement falls at the time of an entry or exit, so it cannot be placed inside or outside the window.',
  'funding-asset': 'A settlement is in an asset outside the listed stablecoins.',
  changing: 'The account’s retained history changed while it was read; the next read shows it.',
  'no-funding-read': 'Settled funding is not read for this venue.',
  'account-disabled': 'The account is disabled.',
}

const settlements = (count: number) => count === 0 ? 'No settlements' : count === 1 ? '1 settlement' : `${count} settlements`

/**
 * The settled funding line beside the Play-local result: the exact sum with its count and window when the play alone held
 * the position, otherwise why it is not shown. It never changes the venue or Play-local result.
 */
export function PlayFundingRow({ funding }: { funding: PlayFunding }) {
  return <div><dt>Settled funding<InfoTip label="About settled funding">{playFundingNote}</InfoTip></dt>
    <dd>{funding.state === 'sole'
      ? <>{funding.amountUsd} {funding.asset ?? 'USD'} <small>{settlements(funding.paymentCount)} · <Time at={funding.fromUtc} /> – <Time at={funding.toUtc} /></small></>
      : <InfoTip className="value-tag" trigger={labels[funding.state]} label="settled funding">
        {`${reasons[funding.reason]}${funding.state === 'incomplete' ? ' Missing funding is not zero.' : ''}`}</InfoTip>}</dd></div>
}
