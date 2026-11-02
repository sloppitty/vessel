import { useEffect, useState } from 'react'
import type { OpenRisk, OpenRiskPreview, OpenRiskPreviewRequest, RiskPlan } from '@/api/risk'
import type { WorkspaceApi } from '@/api/workspace'
import { exactAdd, exactDecimal, exactDiv, exactFixed, exactInteger, exactMul, exactSignOf, exactSub, exactZero, type Exact } from '@/lib/decimal'
import type { PlayDraft } from './draft'

/*
 * Open risk in the browser (Phase 2, item 1). The server computes every Play's risk, including the plan being edited
 * (its preview); the browser only replaces this Play's saved contribution with that preview and forms shares exactly.
 */

/** The parts of the draft open risk reads, as the server's preview takes them. */
export function riskPlanOf(draft: Pick<PlayDraft, 'direction' | 'sizingMode' | 'size' | 'leverage' | 'entries'>): RiskPlan {
  return {
    direction: draft.direction, sizingMode: draft.sizingMode, size: draft.size, leverage: draft.leverage,
    entries: draft.entries.map(entry => ({ id: entry.id, share: entry.share, price: entry.price,
      stops: entry.stops.map(stop => ({ id: stop.id, unit: stop.unit, value: stop.value, share: stop.share })) })),
  }
}

type Load<T> = { key: string; value?: T; failed?: boolean }

/** Whether this client reads open risk: older clients and partial test doubles may not. */
export const openRiskReadable = (api: WorkspaceApi) => typeof api.openRisk === 'function' && typeof api.openRiskPreview === 'function'

/** The scope's open risk, read again whenever `reloadKey` changes. */
export function useOpenRisk(api: WorkspaceApi, scope: string | null, reloadKey: unknown) {
  const key = JSON.stringify([scope, reloadKey])
  const [state, setState] = useState<Load<OpenRisk> & { api: WorkspaceApi } | null>(null)
  useEffect(() => {
    if (scope === null) return
    const controller = new AbortController()
    api.openRisk(scope, controller.signal).then(value => { if (!controller.signal.aborted) setState({ api, key, value }) })
      .catch(() => { if (!controller.signal.aborted) setState({ api, key, failed: true }) })
    return () => controller.abort()
  }, [api, key, scope])
  return state?.api === api && state.key === key ? state : null
}

/**
 * This plan's own risk by the server's rule, read a moment after the owner stops editing, and again whenever `reloadKey`
 * changes, so new fills of the saved Play are counted and an older preview never replaces a fresh total.
 */
export function useOpenRiskPreview(api: WorkspaceApi, request: OpenRiskPreviewRequest | null, reloadKey: unknown) {
  const key = JSON.stringify([request, reloadKey])
  const [state, setState] = useState<Load<OpenRiskPreview> & { api: WorkspaceApi } | null>(null)
  useEffect(() => {
    if (request === null) return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      api.openRiskPreview(request, controller.signal).then(value => { if (!controller.signal.aborted) setState({ api, key, value }) })
        .catch(() => { if (!controller.signal.aborted) setState({ api, key, failed: true }) })
    }, 300)
    return () => { clearTimeout(timer); controller.abort() }
  }, [api, key, request])
  return state?.api === api && state.key === key ? state : null
}

export const exact = (value: string | null) => value === null ? null : exactDecimal(value)

/**
 * When only some of the scope's accounts report a balance, shares divide by their known total: say so, never imply the
 * whole scope.
 */
export const partialBalance = (risk: OpenRisk) => risk.balance.contributingAccountCount < risk.balance.totalAccountCount
  ? ` (known balances of ${risk.balance.contributingAccountCount} of ${risk.balance.totalAccountCount} accounts)` : ''

/** A share of the balance in percent with two decimals; a nonzero share that rounds to zero is "<0.01%". */
export function riskShare(amount: Exact, balance: Exact | null): string | null {
  if (balance === null || exactSignOf(balance) <= 0) return null
  const percent = exactMul(exactDiv(amount, balance)!, exactInteger(100))
  const text = exactFixed(percent, 2)
  return exactSignOf(percent) > 0 && text === '0' ? '<0.01%' : `${text}%`
}

interface Totals { long: Exact; short: Exact; unknown: number }

/** The scope's totals without this Play's saved contribution, then with the edited plan's own risk. */
export function withThisPlay(risk: OpenRisk, playId: string | null, preview: OpenRiskPreview | null): { totals: Totals; own: Exact | null } {
  const totals: Totals = { long: exact(risk.long.knownUsd) ?? exactZero, short: exact(risk.short.knownUsd) ?? exactZero,
    unknown: risk.long.unknownCount + risk.short.unknownCount }
  const saved = playId === null ? undefined : risk.plays.find(play => play.playId === playId)
  if (saved) {
    const amount = exact(saved.riskUsd)
    if (amount === null) totals.unknown -= 1
    else totals[saved.direction] = exactSub(totals[saved.direction], amount)
  }
  const own = preview ? exact(preview.riskUsd) : null
  if (preview) {
    if (own === null) totals.unknown += 1
    else totals[preview.direction] = exactAdd(totals[preview.direction], own)
  }
  return { totals, own }
}

