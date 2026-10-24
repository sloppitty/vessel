/**
 * The Paper playground mark: a folded paper boat, shown in place of the Vessel mark while the workspace is in Paper
 * mode. Lucide has no paper boat; the paths come from the owner-approved trading mode prototype. Colours come from
 * `currentColor`.
 */
export function PaperBoatMark({ size = 24, className }: { size?: number; className?: string }) {
  return <svg className={className} width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"
    strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
    <path d="M2.8 14h18.4l-3.4 6H6.2z" />
    <path d="M12 3.5 6.6 14h10.8z" />
    <path d="M12 3.5V14" />
  </svg>
}
