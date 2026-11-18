import {
  Anchor, Bitcoin, Briefcase, ChartCandlestick, ChartColumn, ChartLine, Coins, Compass, Crown, Flame, Folder, Gem,
  Hourglass, Landmark, Mountain, PiggyBank, Rocket, Shield, Sprout, Target, Timer, TrendingUp, Wallet, Zap, type LucideIcon,
} from 'lucide-react'
import { defaultPortfolioIcon, isPortfolioIconKey, type PortfolioIconKey } from '@/api/portfolio-icons'

/**
 * The label and drawn icon for each portfolio icon key. The keys come from `@/api/portfolio-icons`,
 * which must match `PortfolioIcons` in `backend/src/Vessel.Domain/Workspace/Portfolio.cs`; the type check
 * below fails when a key has no icon here or an icon has no key.
 */
export const portfolioIcons = {
  folder: { label: 'Folder', icon: Folder },
  briefcase: { label: 'Briefcase', icon: Briefcase },
  wallet: { label: 'Wallet', icon: Wallet },
  'piggy-bank': { label: 'Piggy bank', icon: PiggyBank },
  landmark: { label: 'Bank', icon: Landmark },
  coins: { label: 'Coins', icon: Coins },
  bitcoin: { label: 'Bitcoin', icon: Bitcoin },
  gem: { label: 'Gem', icon: Gem },
  'trending-up': { label: 'Trending up', icon: TrendingUp },
  'chart-line': { label: 'Line chart', icon: ChartLine },
  'chart-candlestick': { label: 'Candlesticks', icon: ChartCandlestick },
  'chart-column': { label: 'Column chart', icon: ChartColumn },
  target: { label: 'Target', icon: Target },
  compass: { label: 'Compass', icon: Compass },
  rocket: { label: 'Rocket', icon: Rocket },
  zap: { label: 'Lightning', icon: Zap },
  flame: { label: 'Flame', icon: Flame },
  timer: { label: 'Timer', icon: Timer },
  hourglass: { label: 'Hourglass', icon: Hourglass },
  shield: { label: 'Shield', icon: Shield },
  anchor: { label: 'Anchor', icon: Anchor },
  mountain: { label: 'Mountain', icon: Mountain },
  sprout: { label: 'Sprout', icon: Sprout },
  crown: { label: 'Crown', icon: Crown },
} as const satisfies Record<PortfolioIconKey, { label: string; icon: LucideIcon }>

/** A stored key this browser does not know shows the default folder. */
export const portfolioIconKey = (key: string | undefined): PortfolioIconKey => isPortfolioIconKey(key) ? key : defaultPortfolioIcon
