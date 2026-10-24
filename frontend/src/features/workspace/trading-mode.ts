import type { TradingMode } from '@/api/paper'

/** The trading mode control's names. "Paper" alone is avoided: a light appearance theme has that name. */
export const tradingModeNames: Record<TradingMode, string> = { real: 'Journal', paper: 'Paper playground' }
export const otherTradingMode = (mode: TradingMode): TradingMode => mode === 'real' ? 'paper' : 'real'
/** The label changes on every switch, so the name states the action instead of a pressed state. */
export const tradingModeSwitchName = (mode: TradingMode) => `${tradingModeNames[mode]}, switch to ${tradingModeNames[otherTradingMode(mode)]}`
export const tradingModeDiscardMessage = 'Switch trading mode and discard unsaved Play work? Already-sent changes may still complete in their original mode.'

/** A switch made in this browser session. The shell remounts per mode; this survives it and is read once on mount. */
export interface TradingModeArrival { from: TradingMode; to: TradingMode; focusControl: boolean }
