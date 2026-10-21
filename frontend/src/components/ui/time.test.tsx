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
import { ReviewPage } from '@/features/review/ReviewPage'
import { createWorkspaceApi } from '@/api/workspace'
import { reviewFixture } from '@/test/review-fixture'
import { restoreZone } from '@/test/zone'

const originalZone = process.env.TZ
afterEach(() => {
  restoreZone(originalZone)
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

  it('follows the zone another tab chooses, and Local when storage is cleared', async () => {
    const user = userEvent.setup()
    render(<TimeSettings />)
    const store = (text: string | null) => act(() => {
      if (text === null) localStorage.clear(); else localStorage.setItem('vessel.display', text)
      window.dispatchEvent(new StorageEvent('storage', { key: text === null ? null : 'vessel.display' }))
    })
    store('{"timeZone":"utc"}')
    expect(screen.getByRole('radio', { name: 'UTC' })).toBeChecked()
    store(null)
    expect(screen.getByRole('radio', { name: /^Local/ })).toBeChecked()
    // The choice stores again rather than matching a stale value.
    await user.click(screen.getByRole('radio', { name: 'UTC' }))
    expect(localStorage.getItem('vessel.display')).toBe('{"timeZone":"utc"}')
  })

  it("names Review's close-date zone beside the UTC report periods", async () => {
    process.env.TZ = 'America/Los_Angeles'
    const user = userEvent.setup()
    const api = { ...createWorkspaceApi('test'), review: () => Promise.resolve(reviewFixture) }
    render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={() => Promise.resolve()} />)
    await user.click(await screen.findByRole('button', { name: `${reviewFixture.metrics.count} Plays` }))
    const table = await within(await screen.findByRole('dialog')).findByRole('table', { name: 'Contributing closed Plays' })
    const closed = within(table).getByRole('columnheader', { name: /^Closed \(local\)/ })
    expect(within(closed).getByRole('button', { name: 'About close dates' }))
      .toHaveAccessibleDescription(/local \(America\/Los_Angeles\).*rolling windows in UTC/)
    act(() => displayZoneStore.set('utc'))
    expect(within(table).getByRole('columnheader', { name: /^Closed \(UTC\)/ })).toBeInTheDocument()
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
