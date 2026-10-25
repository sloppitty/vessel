import { useSyncExternalStore } from 'react'
import { Popover } from 'radix-ui'
import { ChevronRight } from 'lucide-react'
import { locationKey, type ShellLocation } from './shell-history'

const subscribe = (callback: () => void) => {
  window.addEventListener('resize', callback)
  return () => window.removeEventListener('resize', callback)
}
const density = () => window.innerWidth <= 360 ? 'compact' : window.innerWidth <= 800 ? 'narrow' : 'desktop'

export function ShellBreadcrumb({ trail, onVisit }: { trail: ShellLocation[]; onVisit: (location: ShellLocation) => void }) {
  const width = useSyncExternalStore(subscribe, density)
  const tail = width === 'compact' ? 1 : 2
  const collapse = width === 'desktop' ? trail.length > 4 : trail.length > tail
  const first = width === 'desktop' && collapse ? trail.slice(0, 1) : []
  const hidden = collapse ? trail.slice(first.length, -tail) : []
  const visible = collapse ? trail.slice(-tail) : trail
  const link = (location: ShellLocation) => <a href={`#${location.page}`} title={location.label}
    aria-label={`${location.label} — previous location`}
    onClick={event => { event.preventDefault(); onVisit(location) }}>{location.label}</a>
  const crumb = (location: ShellLocation, current: boolean) => <li key={locationKey(location)}>
    <ChevronRight aria-hidden="true" size={13} />
    {current ? <span aria-current="page" title={location.label}>{location.label}</span> : link(location)}
  </li>
  return <nav className="shell-breadcrumb" aria-label="Breadcrumb"><ol>
    {first.map(location => crumb(location, false))}
    {hidden.length > 0 && <li className="breadcrumb-overflow">
      <ChevronRight aria-hidden="true" size={13} />
      <Popover.Root><Popover.Trigger asChild><button type="button" aria-label={`Show ${hidden.length} more ${hidden.length === 1 ? 'location' : 'locations'}`}>…</button></Popover.Trigger>
        <Popover.Portal><Popover.Content className="breadcrumb-locations" align="start" sideOffset={8} aria-label="Earlier locations">
          <ol>{hidden.map(location => <li key={locationKey(location)}><Popover.Close asChild>{link(location)}</Popover.Close></li>)}</ol>
        </Popover.Content></Popover.Portal>
      </Popover.Root>
    </li>}
    {visible.map((location, index) => crumb(location, index === visible.length - 1))}
  </ol></nav>
}
