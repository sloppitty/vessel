import { PaperSettings } from '@/features/settings/PaperSettings'
import type { SystemInfo } from '@/api/system'
import { Button } from '@/components/ui/button'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { VenueBadge } from '@/components/venues/VenueBadge'
import { AppearanceSettings } from '@/features/settings/AppearanceSettings'
import { TimeSettings } from '@/features/settings/TimeSettings'
import type { WorkspaceApi } from '@/api/workspace'
import { NotificationSettings } from '@/features/notifications/NotificationSettings'
import { ReviewSettings } from '@/features/review/ReviewSettings'
import { RiskSettings } from '@/features/settings/RiskSettings'
import { ReportingSettings } from '@/features/settings/ReportingSettings'
import { CostSettings } from '@/features/settings/CostSettings'
import { DataExportSettings } from '@/features/settings/DataExportSettings'
import type { InAppNotifications } from '@/features/notifications/useInAppNotifications'
import { ProfileSettings } from '@/features/identity/ProfileSettings'
import type { ShellIdentity } from '@/features/identity/types'
import { Database } from 'lucide-react'

export type SettingsTab = 'appearance' | 'profile'

/** Appearance and workspace preferences beside a Profile & sign-in tab. */
export function SettingsPage({ system, disconnectSession, onAccounts, api, inApp, identity, tab = 'appearance', onTab, onPaperSettingsChange }: {
  system: SystemInfo; disconnectSession: () => void; onAccounts: () => void; api: WorkspaceApi; inApp: InAppNotifications
  identity: ShellIdentity; tab?: SettingsTab; onTab?: (tab: SettingsTab) => void; onPaperSettingsChange?: () => void
}) {
  const paper = api.tradingMode === 'paper'
  const venuesPanel = <section className="shell-panel settings-panel">
    <Database size={23} /><h2>{paper ? 'Paper capabilities' : 'Venue capabilities'}</h2>
    <div className="venue-capability-list">{system.venues.filter(venue => !paper || venue.capabilities.paperExecution).map(venue => <div key={venue.id}>
      <VenueBadge venueId={venue.id} /><span className="workspace-badge">{paper ? 'Paper · simulated execution; no imports or real orders' : <>import {venue.capabilities.sync ? 'available' : 'unavailable'} · orders {venue.capabilities.orders ? 'read-only' : 'unavailable'}</>}</span>
    </div>)}</div>
    <Button variant="outline" onClick={onAccounts}>{paper ? 'Manage paper accounts' : 'Manage account access and refresh'}</Button>
  </section>
  return <Tabs value={tab} onValueChange={value => onTab?.(value === 'profile' ? 'profile' : 'appearance')} className="settings-tabs">
    <TabsList aria-label="Settings sections"><TabsTrigger value="appearance">Appearance</TabsTrigger><TabsTrigger value="profile">Profile &amp; sign-in</TabsTrigger></TabsList>
    <TabsContent value="appearance"><div className="settings-grid"><AppearanceSettings /><TimeSettings /><ReportingSettings api={api} /><PaperSettings api={api} {...(onPaperSettingsChange ? { onChange: onPaperSettingsChange } : {})} />{!paper && <NotificationSettings api={api} inApp={inApp} />}{!paper && <ReviewSettings />}{!paper && <RiskSettings api={api} />}{!paper && <CostSettings venues={system.venues} />}{!paper && <DataExportSettings api={api} />}{venuesPanel}</div></TabsContent>
    <TabsContent value="profile"><ProfileSettings identity={identity} onSignOut={disconnectSession} /></TabsContent>
  </Tabs>
}
