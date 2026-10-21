import { PaperSettings } from '@/features/settings/PaperSettings'
import type { SystemInfo } from '@/api/system'
import { Button } from '@/components/ui/button'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { VenueBadge } from '@/components/venues/VenueBadge'
import { AppearanceSettings, CurrentThemeName } from '@/features/settings/AppearanceSettings'
import { TimeSettings } from '@/features/settings/TimeSettings'
import { showAppearanceSection } from '@/features/settings/appearance'
import type { WorkspaceApi } from '@/api/workspace'
import { NotificationSettings } from '@/features/notifications/NotificationSettings'
import { ReviewSettings } from '@/features/review/ReviewSettings'
import type { InAppNotifications } from '@/features/notifications/useInAppNotifications'
import { ProfileSettings } from '@/features/identity/ProfileSettings'
import type { ShellIdentity } from '@/features/identity/types'
import { Database, LogOut, ShieldCheck } from 'lucide-react'

export type SettingsTab = 'appearance' | 'profile'

export const venueSigningStatement = 'Vessel never signs, sends or approves anything at a venue.'

/** Token mode stacks its panels as before; Wallet mode adds a Profile & sign-in tab in place of Private session. */
export function SettingsPage({ system, disconnectSession, onAccounts, api, inApp, identity, tab = 'appearance', onTab, onPaperSettingsChange }: {
  system: SystemInfo; disconnectSession: () => void; onAccounts: () => void; api: WorkspaceApi; inApp: InAppNotifications
  identity?: ShellIdentity; tab?: SettingsTab; onTab?: (tab: SettingsTab) => void; onPaperSettingsChange?: () => void
}) {
  const paper = api.tradingMode === 'paper'
  const venuesPanel = <section className="shell-panel settings-panel">
    <Database size={23} /><h2>{paper ? 'Paper capabilities' : 'Venue capabilities'}</h2>
    <div className="venue-capability-list">{system.venues.filter(venue => !paper || venue.capabilities.paperExecution).map(venue => <div key={venue.id}>
      <VenueBadge venueId={venue.id} /><span className="workspace-badge">{paper ? 'Paper · simulated execution; no imports or real orders' : <>import {venue.capabilities.sync ? 'available' : 'unavailable'} · orders {venue.capabilities.orders ? 'read-only' : 'unavailable'}</>}</span>
    </div>)}</div>
    <Button variant="outline" onClick={onAccounts}>{paper ? 'Manage paper accounts' : 'Manage account access and refresh'}</Button>
  </section>
  if (!identity) return <div className="settings-grid">
    <AppearanceSettings />
    <TimeSettings />
    <PaperSettings api={api} {...(onPaperSettingsChange ? { onChange: onPaperSettingsChange } : {})} />
    {!paper && <NotificationSettings api={api} inApp={inApp} />}
    {!paper && <ReviewSettings />}
    <section className="shell-panel settings-panel">
      <ShieldCheck size={23} /><h2>Private session</h2>
      <p>Connected as {system.owner.displayName}.</p>
      <dl>
        <div><dt>Orders and signing</dt><dd>{venueSigningStatement}</dd></div>
        <div><dt>Theme</dt><dd className="settings-theme"><CurrentThemeName /><Button variant="outline" size="sm" onClick={() => showAppearanceSection('theme-customizer')}>Customize</Button></dd></div>
      </dl>
      <Button variant="outline" onClick={disconnectSession}><LogOut size={15} />Disconnect session</Button>
    </section>
    {venuesPanel}
  </div>
  return <Tabs value={tab} onValueChange={value => onTab?.(value === 'profile' ? 'profile' : 'appearance')} className="settings-tabs">
    <TabsList aria-label="Settings sections"><TabsTrigger value="appearance">Appearance</TabsTrigger><TabsTrigger value="profile">Profile &amp; sign-in</TabsTrigger></TabsList>
    <TabsContent value="appearance"><div className="settings-grid"><AppearanceSettings /><TimeSettings /><PaperSettings api={api} {...(onPaperSettingsChange ? { onChange: onPaperSettingsChange } : {})} />{!paper && <NotificationSettings api={api} inApp={inApp} />}{!paper && <ReviewSettings />}{venuesPanel}</div></TabsContent>
    <TabsContent value="profile"><ProfileSettings identity={identity} onSignOut={disconnectSession} /></TabsContent>
  </Tabs>
}
