import { Info } from 'lucide-react'
import { Button } from '@/components/ui/button'

/**
 * `tone: 'paper'` draws the notice in the Paper tone, so it matches Paper mode's chrome. A notice that can appear while
 * the page is open sets `announce`, and `action` adds one button after its text.
 */
export interface ShellNotice {
  id: string; title: string; message: string; tone?: 'paper'; announce?: boolean
  action?: { label: string; onClick: () => void }
}

/** Mode-level context only. Operational alerts stay with their existing owners. */
export function ShellNotices({ notices }: { notices: readonly ShellNotice[] }) {
  if (!notices.length) return null
  return <div className="shell-notices">{notices.map(notice => <div className={`shell-notice${notice.tone ? ` is-${notice.tone}` : ''}`} role={notice.announce ? 'status' : 'note'} key={notice.id}>
    <Info size={15} aria-hidden="true" /><span><strong>{notice.title}</strong> {notice.message}</span>
    {notice.action && <Button type="button" variant="outline" size="sm" className="shell-notice-action" onClick={notice.action.onClick}>{notice.action.label}</Button>}
  </div>)}</div>
}
