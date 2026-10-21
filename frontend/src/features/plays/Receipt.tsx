import type { ManualExecutions } from '@/api/diary'
import type { EntryProgress, PlayExecution, PlayReceipt, PlayStatus } from '@/api/plays'
import { InfoTip } from '@/components/ui/info-tip'
import { decimalDifference } from '@/lib/decimal'
import { amount } from '@/lib/format'
import { duration } from '@/features/workspace/activity-format'
import { multiple, timeHeld } from './receipt-format'
import type { DraftEntry } from './draft'
import { LocalResultValue, SignDiffersFlag, localResultNote } from './LocalResult'

/*
 * What happened beside what was planned (phase 1, 1.1a). Every figure comes from the server: planned values from the plan
 * revision in force at each entry's first fill, and R by Review's own rule, so the receipt never disagrees with Review.
 */

export const estimatedRNote = 'Estimated R (planned-stop model): net result ÷ initial planned risk, which is executed entry notional × ' +
  'the share-weighted stop distance of the plan in force at the first entry fill. Only a closed Play Review scores, with a ' +
  'complete stop for every entry, has one. An estimate, not the actual loss risk.'

const signed = (value: string) => value.startsWith('-') ? value : `+${value}`

/**
 * Each planned entry beside its plan: average fill against the planned price, filled against the planned quantity. While the
 * execution is stale (just after a plan revision is saved, say) its figures may compare with an earlier plan, so they wait.
 */
export function EntryReceipts({ progress, entries, stale = false }: { progress: readonly EntryProgress[]; entries: readonly DraftEntry[]; stale?: boolean }) {
  // An older server sends no planned values; nothing is guessed from the current plan in their place.
  const shown = progress.filter(entry => entry.plannedPrice !== undefined)
  if (!shown.length) return null
  return <section aria-label="Entries against the plan" className="execution-section receipt-entries">
    <h3>Against the plan<InfoTip label="About the plan comparison">Each entry beside the plan revision in force at its first fill, or the current plan before it fills.</InfoTip></h3>
    {stale ? <p className="muted" role="status">Unknown until the execution is read again.</p> : <ul className="execution-list">{shown.map(entry => {
      const index = entries.findIndex(item => item.id === entry.entryId)
      const difference = entry.averageFillPrice && entry.plannedPrice ? decimalDifference(entry.averageFillPrice, entry.plannedPrice) : null
      return <li key={entry.entryId}>
        <span><strong>{entries[index]?.name || `Entry ${index + 1 || ''}`.trim()}</strong>
          {entry.planRevision != null && <small>Plan revision {entry.planRevision}</small>}</span>
        <span className="receipt-figures">
          <span>Average fill {entry.averageFillPrice ?? 'none yet'} vs plan {entry.plannedPrice ?? 'unknown'}{difference !== null && ` (${signed(difference)})`}</span>
          <span title={entry.plannedQuantity ?? undefined}>Filled {entry.filledQuantity} of {entry.plannedQuantity == null ? 'unknown' : amount(entry.plannedQuantity)}</span>
        </span>
      </li>
    })}</ul>}
  </section>
}

/** Estimated R with the initial risk it divides by, or Unknown opening the reason. */
export function EstimatedR({ receipt }: { receipt: PlayReceipt }) {
  return receipt.estimatedR === null
    ? <InfoTip className="value-tag" trigger="Unknown" label="estimated R">{receipt.estimatedRReason}</InfoTip>
    : <><span title={receipt.estimatedR}>{multiple(receipt.estimatedR)}</span> <small>÷ {receipt.initialRisk} initial risk</small></>
}

/** The receipt's own rows for an execution totals list. */
export function ReceiptTotals({ receipt, status }: { receipt: PlayReceipt; status: PlayStatus }) {
  const held = timeHeld(receipt, status)
  return <>
    <div><dt>Estimated R<InfoTip label="About estimated R">{estimatedRNote}</InfoTip></dt><dd><EstimatedR receipt={receipt} /></dd></div>
    {held && <div><dt>Time held</dt><dd>{held}</dd></div>}
  </>
}

/** The results a receipt sits beside: a venue Play's two bases, or a manual Play's own ledger. */
export type ReceiptResult = { kind: 'venue'; totals: PlayExecution['totals'] }
  | { kind: 'manual'; totals: ManualExecutions['totals']; fees: Pick<ManualExecutions, 'fees' | 'unknownFees'> }

/** The compact receipt at the top of the Review tab, so a review is written beside the outcome. */
export function ReviewReceipt({ receipt, result, status }: { receipt: PlayReceipt; result: ReceiptResult; status: PlayStatus }) {
  const held = timeHeld(receipt, status)
  return <dl className="execution-totals review-receipt" aria-label="Outcome">
    {result.kind === 'venue' ? <>
      <div><dt>Venue net</dt><dd>{result.totals.venueNetUsd == null
        ? <InfoTip className="value-tag" trigger="Unknown" label="venue net">A fee is unverified or in a token outside the listed stablecoins.</InfoTip>
        : `${result.totals.venueNetUsd} USD`}</dd></div>
      {result.totals.local && <div><dt>Play-local result<InfoTip label="About the Play-local result">{localResultNote}</InfoTip></dt>
        <dd><LocalResultValue local={result.totals.local} format={value => `${value} USD`} />{result.totals.local.signDiffers && <SignDiffersFlag />}</dd></div>}
      <div><dt>Fees</dt><dd>{result.totals.fees.length ? result.totals.fees.map(fee => `${fee.amount} ${fee.token}`).join(' · ') : '0'}</dd></div>
    </> : <>
      <div><dt>Manual ledger net</dt><dd>{result.totals.netResultUsd === null
        ? <InfoTip className="value-tag" trigger="Unknown" label="manual ledger net">A cost is unknown or not in a supported USD currency.</InfoTip>
        : `${result.totals.netResultUsd} USD`}</dd></div>
      <div><dt>Fees</dt><dd><ManualFees ledger={result.fees} /></dd></div>
    </>}
    <div><dt>Estimated R<InfoTip label="About estimated R">{estimatedRNote}</InfoTip></dt><dd><EstimatedR receipt={receipt} /></dd></div>
    {held && <div><dt>Time held</dt><dd>{held}</dd></div>}
  </dl>
}

/** A manual ledger's known fees by currency, and how many executions have unknown costs, which are never zero. */
export function ManualFees({ ledger }: { ledger: Pick<ManualExecutions, 'fees' | 'unknownFees'> }) {
  const known = ledger.fees?.length ? ledger.fees.map(fee => `${fee.amount} ${fee.token}`).join(' · ') : ledger.fees ? '0' : null
  const unknown = ledger.unknownFees ? `${ledger.unknownFees} unknown` : null
  return <>{[known, unknown].filter(Boolean).join(' · ') || 'Not reported'}</>
}

/** The facts of a retrospective Play, which has no plan to compare with: what was traded, at what prices and result. */
export function RetrospectiveReceipt({ execution, direction }: { execution: PlayExecution; direction: 'long' | 'short' }) {
  const { totals, receipt } = execution
  return <dl className="retrospective-receipt" aria-label="Trade facts">
    <div><dt>Direction</dt><dd>{direction === 'long' ? 'Long' : 'Short'}</dd></div>
    <div><dt>Entered</dt><dd>{totals.enteredQuantity}</dd></div>
    <div><dt>Exited</dt><dd>{totals.exitedQuantity}</dd></div>
    {receipt && <>
      <div><dt>Average entry</dt><dd>{receipt.averageEntryPrice ?? 'Unknown'}</dd></div>
      <div><dt>Average exit</dt><dd>{receipt.averageExitPrice ?? 'Unknown'}</dd></div>
    </>}
    <div><dt>Venue net</dt><dd>{totals.venueNetUsd == null ? 'Unknown' : `${totals.venueNetUsd} USD`}</dd></div>
    {totals.local && <div><dt>Play-local result</dt><dd><LocalResultValue local={totals.local} format={value => `${value} USD`} />
      {totals.local.signDiffers && <SignDiffersFlag />}</dd></div>}
    <div><dt>Fees</dt><dd>{totals.fees.length ? totals.fees.map(fee => `${fee.amount} ${fee.token}`).join(' · ') : '0'}</dd></div>
    {receipt && <>
      {receipt.firstEntryAtUtc && receipt.lastExitAtUtc && <div><dt>Time held</dt><dd>{duration(receipt.firstEntryAtUtc, receipt.lastExitAtUtc)}</dd></div>}
      <div><dt>Fills</dt><dd>{receipt.fillCount}</dd></div>
    </>}
  </dl>
}
