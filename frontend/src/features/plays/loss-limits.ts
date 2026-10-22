import { useEffect, useState } from 'react'
import type { PreflightCheck } from '@/api/plays'
import type { LossLimits, LossWindow } from '@/api/risk'
import type { WorkspaceApi } from '@/api/workspace'
import { exactDecimal, exactFixed } from '@/lib/decimal'

/*
 * The soft daily and weekly loss limits (Phase 2, item 10b): today's and this week's loss in R, by the reporting setting's
 * days and weeks, against the owner's limits. A breach is flagged in capital context, Suggestions and the pre-flight;
 * nothing is blocked.
 */

export type LossLimitsLoad = { value: LossLimits } | { failed: true }

export const lossLimitsReadable = (api: WorkspaceApi) => typeof api.lossLimits === 'function'

/** The loss limits, read again whenever `reloadKey` changes; null while loading or when not read. */
export function useLossLimits(api: WorkspaceApi, enabled: boolean, reloadKey: unknown): LossLimitsLoad | null {
  const key = JSON.stringify(reloadKey)
  const [state, setState] = useState<{ api: WorkspaceApi; key: string; load: LossLimitsLoad } | null>(null)
  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    api.lossLimits(controller.signal).then(value => { if (!controller.signal.aborted) setState({ api, key, load: { value } }) })
      .catch(() => { if (!controller.signal.aborted) setState({ api, key, load: { failed: true } }) })
    return () => controller.abort()
  }, [api, enabled, key])
  return enabled && state?.api === api && state.key === key ? state.load : null
}

/** An exact R figure to two places, with a typographic minus: "−1.25R". */
export const rText = (value: string) => {
  const exact = exactDecimal(value)
  const text = exact === null ? value : exactFixed(exact, 2)
  return `${text.startsWith('-') ? `−${text.slice(1)}` : text}R`
}

const windows = (loss: LossLimits): Array<[string, LossWindow]> => [['today', loss.day], ['this week', loss.week]]
export const hasLossLimit = (loss: LossLimits) => loss.day.limitR !== null || loss.week.limitR !== null

/** "Today's loss −2.1R reached your 2R daily limit; this week's …" for each reached limit, or null when none is. */
export function lossLimitBreach(loss: LossLimits): string | null {
  const reached = windows(loss).filter(([, window]) => window.breached)
    .map(([label, window]) => `${label === 'today' ? 'Today' : 'This week'}'s loss ${rText(window.netR)} reached your ${rText(window.limitR!)} ${label === 'today' ? 'daily' : 'weekly'} limit`)
  return reached.length ? `${reached.join('; ')}. Nothing is blocked.` : null
}

/**
 * The pre-flight check, or null when no limit is set or the read failed: a failed read cannot tell whether a limit exists,
 * and capital context says it is unavailable. Skipped when a limit is reached; unknown when a Play without R could still
 * reach one; passed otherwise. A Play without R is never counted as zero.
 */
export function lossLimitCheck(load: LossLimitsLoad | null | undefined): PreflightCheck | null {
  if (!load || 'failed' in load) return null
  const loss = load.value
  if (!hasLossLimit(loss)) return null
  if (loss.day.breached || loss.week.breached) return { code: 'loss-limit', outcome: 'skipped' }
  const undecided = windows(loss).some(([, window]) => window.limitR !== null && window.withoutR > 0)
  return { code: 'loss-limit', outcome: undecided ? 'unknown' : 'passed' }
}
