import { Info } from 'lucide-react'

export interface ShellNotice { id: string; title: string; message: string }

/** Mode-level context only. Operational alerts stay with their existing owners. */
export function ShellNotices({ notices }: { notices: readonly ShellNotice[] }) {
  if (!notices.length) return null
  return <div className="shell-notices">{notices.map(notice => <div className="shell-notice" role="note" key={notice.id}>
    <Info size={15} aria-hidden="true" /><span><strong>{notice.title}</strong> {notice.message}</span>
  </div>)}</div>
}
