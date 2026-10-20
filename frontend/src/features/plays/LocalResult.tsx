import type { PlayLocalResult } from '@/api/local-result'
import { InfoTip } from '@/components/ui/info-tip'

/** What the Play-local result is, for its tips. Kept short; the docs carry the gates. */
// Whether venue execution P&L includes funding is unverified for every venue (venue-funding.md), so either basis can differ by it.
export const fundingNote = 'Whether venue-reported P&L includes funding is unverified'
export const localResultNote = 'This play’s own fills: sell proceeds minus buy costs, less every trading fee. No funding. Shown beside the venue result, never scored.'

/** A known Play-local amount, formatted by the caller, or Unknown opening its reason. */
export function LocalResultValue({ local, format }: { local: PlayLocalResult; format: (value: string) => string }) {
  return local.netUsd === null
    ? <InfoTip className="value-tag" trigger="Unknown" label="Play-local result">{local.reason}</InfoTip>
    : <>{format(local.netUsd)}</>
}

/** The flag where the venue and Play-local results differ in sign; a warning pill named by its text. */
export function SignDiffersFlag() {
  return <InfoTip className="workspace-badge warning-badge coverage-badge sign-differs" trigger="Sign differs by basis">
    The venue result and this play’s own fills disagree on win, loss or scratch. Win, loss and scratch still follow the venue result.
  </InfoTip>
}
