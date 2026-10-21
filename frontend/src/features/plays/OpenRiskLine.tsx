import { useMemo } from 'react'
import type { OpenRiskPreviewRequest } from '@/api/risk'
import type { WorkspaceApi } from '@/api/workspace'
import { exactAdd, exactDiv, exactFixed, exactInteger, exactMul, exactSignOf, exactSub, type Exact } from '@/lib/decimal'
import { money } from '@/lib/format'
import { InfoTip } from '@/components/ui/info-tip'
import type { PlayDraft } from './draft'
import { exact, partialBalance, riskPlanOf, riskShare, useOpenRisk, useOpenRiskPreview, withThisPlay } from './open-risk'

/**
 * One line in capital context (Phase 2, item 1): open risk at the plan's stops across the scope's Planned and Open Plays,
 * long and short apart, the unknown count, and this Play's own increment against the owner's soft limit. Nothing is
 * blocked; it reads "unavailable" rather than a guess when either read fails.
 */
export function OpenRiskLine({ api, scope, reference, playId, accountId, draft, reloadKey }: {
  api: WorkspaceApi; scope: string; reference: string; playId: string | null; accountId: string
  draft: Pick<PlayDraft, 'direction' | 'sizingMode' | 'size' | 'leverage' | 'entries' | 'instrumentSource'>; reloadKey: unknown
}) {
  const plan = useMemo(() => riskPlanOf(draft), [draft])
  const instrumentSource = draft.instrumentSource
  const request = useMemo<OpenRiskPreviewRequest>(() => ({ playId, accountId, plan, instrumentSource }), [playId, accountId, plan, instrumentSource])
  const risk = useOpenRisk(api, scope, reloadKey)
  const preview = useOpenRiskPreview(api, request, reloadKey)
  if (risk?.failed || preview?.failed) return <p className="capital-open-risk" data-testid="open-risk-line">
    <span className="capital-open-risk-label">Open risk</span> unavailable</p>
  if (!risk?.value || !preview?.value) return <p className="capital-open-risk" data-testid="open-risk-line" aria-busy="true">
    <span className="capital-open-risk-label">Open risk</span> …</p>
  const document = risk.value
  const { totals, own } = withThisPlay(document, playId, preview.value)
  const balance = exact(document.balance.amountUsd)
  const show = (amount: Exact) => riskShare(amount, balance) ?? money(exactFixed(amount, 28))
  const total = exactAdd(totals.long, totals.short)
  const limit = exact(document.limitPercent)
  const totalPercent = balance && exactSignOf(balance) > 0 ? exactMul(exactDiv(total, balance)!, exactInteger(100)) : null
  const over = limit !== null && totalPercent !== null && exactSignOf(exactSub(totalPercent, limit)) > 0
  const notIncluded = document.notIncluded.reduce((sum, item) => sum + item.count, 0)
  const { notCovered, unverified } = document.protection
  return <p className="capital-open-risk" data-testid="open-risk-line">
    <InfoTip className="capital-open-risk-label" trigger="Open risk">{document.notice}</InfoTip>
    <span>long {show(totals.long)} · short {show(totals.short)}{balance ? ` of ${reference}${partialBalance(document)}` : ''}</span>
    {totals.unknown > 0 && <span className="capital-open-risk-unknown"> · {totals.unknown} unknown</span>}
    {notIncluded > 0 && <span> · {notIncluded} not included</span>}
    {notCovered > 0 && <span className="capital-open-risk-over"> · {notCovered} not covered at the venue</span>}
    {unverified > 0 && <span> · {unverified} with unverified stops</span>}
    <span> — this Play {own === null ? `unknown (${preview.value.reason})` : `+${show(own)}`}</span>
    {limit !== null && <span className={over ? 'capital-open-risk-over' : undefined}>
      {` (${over ? 'over the' : 'limit'} ${exactFixed(limit, 4)}%${over ? ' limit' : ''})`}</span>}
  </p>
}
