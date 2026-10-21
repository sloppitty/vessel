import type { WorkspaceApi } from '@/api/workspace'
import { exactAdd, exactDecimal, exactDiv, exactFixed, exactInteger, exactMul, exactSignOf, exactSub, exactZero } from '@/lib/decimal'
import { money } from '@/lib/format'
import { InfoTip } from '@/components/ui/info-tip'
import { partialBalance, riskShare, useOpenRisk } from '@/features/plays/open-risk'

/**
 * The Overview's open-risk tile for the selected scope (Phase 2, item 1): long and short at the plans' stops, never
 * netted, the unknown count, and the owner's soft limit. It sits among the metric tiles, so Allocation stays between
 * the tiles and Summary.
 */
export function OverviewOpenRisk({ api, scope, reloadKey }: { api: WorkspaceApi; scope: string; reloadKey: unknown }) {
  const state = useOpenRisk(api, scope, reloadKey)
  const risk = state?.value
  const balance = risk?.balance.amountUsd ? exactDecimal(risk.balance.amountUsd) : null
  const long = risk ? exactDecimal(risk.long.knownUsd) ?? exactZero : exactZero
  const short = risk ? exactDecimal(risk.short.knownUsd) ?? exactZero : exactZero
  const show = (amount: typeof long) => riskShare(amount, balance) ?? money(exactFixed(amount, 28))
  const unknown = risk ? risk.long.unknownCount + risk.short.unknownCount : 0
  const limit = risk?.limitPercent ? exactDecimal(risk.limitPercent) : null
  // Long plus short is a sum of risks, never a net.
  const total = balance && exactSignOf(balance) > 0 ? exactMul(exactDiv(exactAdd(long, short), balance)!, exactInteger(100)) : null
  const over = limit !== null && total !== null && exactSignOf(exactSub(total, limit)) > 0
  return <div className="shell-panel metric" data-testid="overview-open-risk" aria-busy={!state}>
    <span className="metric-label">{risk ? <InfoTip trigger="Open risk">{risk.notice}</InfoTip> : 'Open risk'}</span>
    <strong>{state?.failed ? 'Unavailable' : risk ? `${show(long)} · ${show(short)}` : '—'}</strong>
    {risk && <small>long · short{balance ? ` of balance${partialBalance(risk)}` : ''}{unknown > 0 ? ` · ${unknown} unknown` : ''}
      {risk.notIncluded.map(item => ` · ${item.count} ${item.reason} not included`).join('')}
      {risk.protection.unverified > 0 ? ` · ${risk.protection.unverified} with unverified stops` : ''}</small>}
    {risk && risk.protection.notCovered > 0 && <small className="overview-open-risk-over">{risk.protection.notCovered} not covered at the venue</small>}
    {risk && limit !== null && <small className={over ? 'overview-open-risk-over' : undefined}>
      {over ? `Over the ${exactFixed(limit, 4)}% limit` : `Limit ${exactFixed(limit, 4)}%`}</small>}
  </div>
}
