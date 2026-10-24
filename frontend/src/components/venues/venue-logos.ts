import { NotebookPen, type LucideIcon } from 'lucide-react'
import type { VenueLogoKey } from '@/api/venue-logos'
import hyperliquidLogo from '@/assets/venues/hyperliquid.svg'
import lighterLogo from '@/assets/venues/lighter.svg'
import lighterRobinhoodLogo from '@/assets/venues/lighter-rh.svg'
import risexLogo from '@/assets/venues/risex.svg'
import pacificaLogo from '@/assets/venues/pacifica.svg'

/** `accent` is the venue's brand colour, used only to tint identity marks (venue tag, avatar halo); it is mixed with theme
 * tokens, never used as a surface or text colour on its own. Logos without one stay neutral. */
export type VenueLogo = ({ label: string; src: string } | { label: string; icon: LucideIcon }) & { accent?: string }

/**
 * The venue logos this browser bundles. The API reports one of these keys per venue; keep them in step with the
 * backend venue logo list. Assets are same-origin files served with the app; the browser never fetches venue sites.
 * Trademarks only identify the venue an owner's own account lives on.
 */
export const venueLogos = {
  hyperliquid: { label: 'Hyperliquid', src: hyperliquidLogo, accent: '#97fce4' },
  lighter: { label: 'Lighter', src: lighterLogo, accent: '#c9ced6' },
  'lighter-rh': { label: 'Lighter on Robinhood Chain', src: lighterRobinhoodLogo, accent: '#ccff00' },
  risex: { label: 'RISEx', src: risexLogo, accent: '#02e082' },
  pacifica: { label: 'Pacifica', src: pacificaLogo, accent: '#60d2eb' },
  manual: { label: 'Manual', icon: NotebookPen },
} as const satisfies Record<VenueLogoKey, VenueLogo>
