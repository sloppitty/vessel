import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const here = dirname(fileURLToPath(import.meta.url))
const identity = readFileSync(join(here, 'identity.css'), 'utf8')
const styles = readFileSync(join(here, '../../styles.css'), 'utf8')
/** The declarations of the first rule whose selector list is exactly `selector`. */
const rule = (css: string, selector: string) => {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  return new RegExp(`(?:^|[}\\n])\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(css)?.[1] ?? ''
}

describe('sign-in styles', () => {
  it('never sets text below 11px', () => {
    for (const [, size] of identity.matchAll(/font-size:\s*(\d+(?:\.\d+)?)px/g)) expect(Number(size)).toBeGreaterThanOrEqual(11)
    expect(rule(identity, '.sign-in-steps i')).toContain('font-size: var(--text-2xs)')
  })

  it('keeps a long wallet name from widening the card or its buttons', () => {
    expect(rule(identity, '.sign-in-card, .identity-dialog')).toContain('overflow-wrap: anywhere')
    expect(rule(identity, '.sign-in-body')).toContain('grid-template-columns: minmax(0, 1fr)')
    expect(rule(identity, '.sign-flow')).toContain('grid-template-columns: minmax(0, 1fr)')
    expect(rule(identity, '.identity-truncate')).toMatch(/min-width: 0;.*text-overflow: ellipsis;.*white-space: nowrap/)
    expect(rule(identity, '.identity-row-meta strong')).toContain('text-overflow: ellipsis')
    expect(rule(identity, '.sign-in-actions .sign-in-primary')).toContain('min-width: 0')
    // A content-sized basis lets the other actions wrap under the primary one instead of clipping its label.
    expect(rule(identity, '.sign-in-actions .sign-in-primary')).toContain('flex: 1 1 auto')
    expect(rule(identity, '.message-timer')).toContain('white-space: nowrap')
  })

  it('clips the contour backdrop instead of letting the page scroll', () => {
    const page = rule(styles, '.connection-page')
    expect(page).toContain('overflow: clip')
    expect(page).not.toContain('overflow: hidden')
  })

  it('keeps card-wide form rules off the sign-in forms', () => {
    expect(styles).not.toMatch(/\.connection-card form/)
  })
})
