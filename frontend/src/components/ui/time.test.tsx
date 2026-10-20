import { act, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { render } from '@/test/render'
import { accountFixture } from '@/test/workspace-fixture'
import { activityFillFixture } from '@/test/activity-fixture'
import { displayZoneStore } from '@/lib/display-zone'
import { ActivityTable } from '@/features/workspace/ActivityTable'
import { fillMarkers } from '@/features/plays/chart-fills'
import { TimeSettings } from '@/features/settings/TimeSettings'
import { Time } from './time'

const originalZone = process.env.TZ
afterEach(() => {
  process.env.TZ = originalZone
  localStorage.clear()
  displayZoneStore.reload()
})

describe('display time zone (phase 1, 1.6e)', () => {
  it('switches every shown time between the browser zone and UTC, with the other zone in the tooltip', async () => {
    process.env.TZ = 'Europe/Madrid'
    const user = userEvent.setup()
    render(<><Time at="2026-10-13T10:00:00Z" /><TimeSettings /></>)
    expect(screen.getByRole('radio', { name: 'Local (Europe/Madrid)' })).toBeChecked()
    const shown = screen.getByText('13 Oct 2026, 12:00 CEST')
    expect(shown).toHaveAttribute('title', '13 Oct 2026, 10:00:00 UTC')
    expect(shown).toHaveAttribute('datetime', '2026-10-13T10:00:00Z')
    await user.click(screen.getByRole('radio', { name: 'UTC' }))
    expect(screen.getByText('13 Oct 2026, 10:00 UTC')).toHaveAttribute('title', '13 Oct 2026, 12:00:00 CEST')
    expect(localStorage.getItem('vessel.display')).toBe('{"timeZone":"utc"}')
  })

  it('shows one fill at one time in Activity and on the chart, under either setting (U17)', () => {
    process.env.TZ = 'Europe/Madrid'
    const fill = { ...activityFillFixture, accountId: accountFixture.id, occurredAtUtc: '2026-10-13T10:00:00Z' }
    const chartFill = { id: fill.id, atUtc: fill.occurredAtUtc, price: '100', quantity: '1', side: 'buy' as const, role: 'entry' as const }
    for (const [zone, minute, second] of [['local', '13 Oct 2026, 12:00 CEST', '13 Oct 2026, 12:00:00 CEST'],
      ['utc', '13 Oct 2026, 10:00 UTC', '13 Oct 2026, 10:00:00 UTC']] as const) {
      act(() => displayZoneStore.set(zone))
      const view = render(<ActivityTable fills={[fill]} accounts={[accountFixture]} />)
      expect(within(view.container).getByText(minute)).toBeInTheDocument()
      expect(fillMarkers([chartFill], '1h', zone)[0]!.tooltip).toContain(second)
      view.unmount()
    }
  })
})
