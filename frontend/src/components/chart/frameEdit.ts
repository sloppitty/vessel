import { significantDecimals, tickOf, type PriceStep } from '@/lib/price-step'
/** Draft publications are frame-batched; the renderer keeps its own immediate preview. */
export class FrameEdit<T> {
  private frame: number | null = null
  private pending: T | null = null
  private published: T | null

  /** `start` is the value already shown, so a first move that rounds to it publishes nothing. */
  constructor(private readonly publish: (value: T, phase: 'move' | 'end') => void,
    private readonly same?: (before: T, after: T) => boolean, start: T | null = null) {
    this.published = start
  }

  move(value: T) {
    this.pending = value
    if (this.frame !== null) return
    this.frame = requestAnimationFrame(() => {
      this.frame = null
      const next = this.pending!
      this.pending = null
      if (this.published !== null && this.same?.(this.published, next)) return
      this.published = next
      this.publish(next, 'move')
    })
  }

  /** End carries the final value itself, even if no animation frame has run yet. */
  end(value: T) {
    this.cancel()
    this.publish(value, 'end')
  }

  cancel() {
    if (this.frame !== null) cancelAnimationFrame(this.frame)
    this.frame = null
    this.pending = null
    this.published = null
  }
}

/** Same rounding as chart-plan edits: venue tick, otherwise five significant figures / eight decimals / the venue's cap. */
export function roundedLevelPrice(price: number, step?: PriceStep) {
  const tick = tickOf(step)
  if (tick !== null) {
    let places = 0
    while (places < 12 && Math.abs(Math.round(tick * 10 ** places) - tick * 10 ** places) > 1e-9) places++
    return Number((Math.round(price / tick) * tick).toFixed(places))
  }
  return Number(price.toFixed(significantDecimals(price, step)))
}
