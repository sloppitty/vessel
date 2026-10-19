import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { ChevronDown, Search } from 'lucide-react'
import { Popover } from 'radix-ui'
import { useVenues } from '@/api/venues'
import { VenueLogo } from '@/components/venues/VenueBadge'
import { shortSource } from '@/lib/format'
import { AccountAvatar, AccountName, IdentityTile, SourceChip, VenueTag } from './AccountIdentity'
import { accountLabel, accountTitle, ambiguousAccounts, type IdentityAccount } from './identity'
import './account-picker.css'

/** An account choice. `value` defaults to the ID; `detail` is a secondary line such as its portfolio. */
export interface PickerAccount extends IdentityAccount { value?: string; detail?: string | null | undefined; balance?: string | null | undefined }
/** A choice that is not one account, such as All accounts or a portfolio. Choices sharing a `group` sit under that heading. */
export interface PickerChoice { value: string; label: string; detail?: string | undefined; icon?: ReactNode; group?: string | undefined }

type Item = { kind: 'choice'; value: string; choice: PickerChoice } | { kind: 'account'; value: string; account: PickerAccount }
const valueOf = (a: PickerAccount) => a.value ?? a.id

/**
 * The account selector: a button showing the selected account's venue logo and name (plus its source when another
 * account shares the name) that opens a searchable list with venue filters. `labelledBy` names an element holding the
 * field's visible label; the button's accessible name is that label followed by the current value. `showVenue` adds the
 * venue tag to the trigger; `missingLabel` names a value that matches no listed choice or account (otherwise the
 * placeholder shows); `density="bare"` drops the border and background for dense field bars, like `NativeSelect`.
 * Keyboard: arrow keys open the list and move through it, Enter chooses, Escape closes and returns to the button.
 */
export function AccountPicker({ labelledBy, id, value, onChange, accounts, choices = [], accountsHeading, placeholder = 'Choose an account',
  missingLabel, searchLabel = 'Search accounts', showVenue = false, density, disabled = false, className = '' }: {
  labelledBy: string; id?: string | undefined; value: string; onChange: (value: string) => void
  accounts: readonly PickerAccount[]; choices?: readonly PickerChoice[]; accountsHeading?: string | undefined
  placeholder?: string; missingLabel?: string | undefined; searchLabel?: string; showVenue?: boolean; density?: 'bare' | undefined
  disabled?: boolean; className?: string
}) {
  const venues = useVenues()
  const base = useId()
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [venue, setVenue] = useState<string | null>(null)
  const [active, setActive] = useState(0)
  const input = useRef<HTMLInputElement>(null)
  const ambiguous = useMemo(() => ambiguousAccounts(accounts), [accounts])
  const venueIds = useMemo(() => [...new Set(accounts.map(a => a.venueId))], [accounts])

  const needle = query.trim().toLowerCase()
  const has = (...texts: (string | null | undefined)[]) => !needle || texts.some(t => t?.toLowerCase().includes(needle))
  const shownChoices = venue === null ? choices.filter(c => has(c.label, c.detail)) : []
  const shownAccounts = accounts.filter(a => (venue === null || a.venueId === venue) &&
    has(a.name, venues.name(a.venueId), a.source, shortSource(a.source), a.detail))
  const items: Item[] = [...shownChoices.map(choice => ({ kind: 'choice' as const, value: choice.value, choice })),
    ...shownAccounts.map(account => ({ kind: 'account' as const, value: valueOf(account), account }))]
  const optionId = (index: number) => `${base}-option-${index}`

  const selectedChoice = choices.find(c => c.value === value)
  const selectedAccount = selectedChoice ? undefined : accounts.find(a => valueOf(a) === value)
  const empty = selectedChoice || selectedAccount ? null : value && missingLabel ? missingLabel : placeholder

  const openWith = (next: boolean) => {
    if (next) {
      setQuery(''); setVenue(null)
      const all: Item[] = [...choices.map(choice => ({ kind: 'choice' as const, value: choice.value, choice })), ...accounts.map(account => ({ kind: 'account' as const, value: valueOf(account), account }))]
      setActive(Math.max(0, all.findIndex(item => item.value === value)))
    }
    setOpen(next)
  }
  const choose = (next: string) => { setOpen(false); if (next !== value) onChange(next) }
  useEffect(() => { if (open) document.getElementById(optionId(active))?.scrollIntoView?.({ block: 'nearest' }) })

  const onSearchKey = (event: KeyboardEvent) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      setActive(i => Math.min(Math.max(i + (event.key === 'ArrowDown' ? 1 : -1), 0), Math.max(items.length - 1, 0)))
    } else if (event.key === 'Enter') {
      event.preventDefault()
      const item = items[active]
      if (item) choose(item.value)
    }
  }
  const narrow = (next: { query?: string; venue?: string | null }) => {
    if (next.query !== undefined) setQuery(next.query)
    if (next.venue !== undefined) setVenue(next.venue)
    setActive(0)
  }

  // Spoken names, comma-separated; the visual marks inside are inline and would otherwise run together.
  const spoken = (...parts: (string | null | undefined)[]) => parts.filter(Boolean).join(', ')
  const accountSpoken = (a: PickerAccount, withSource = true) =>
    spoken(accountLabel(a), venues.name(a.venueId), withSource && a.source !== undefined && a.name.trim() ? shortSource(a.source) ?? 'Manual record' : null)
  const option = (item: Item, index: number) => {
    const selected = item.value === value
    // React refuses `key` inside a spread; it is passed on its own.
    const key = `${item.kind}:${item.value}`
    const common = {
      id: optionId(index), role: 'option' as const, 'aria-selected': selected, 'data-value': item.value,
      className: `account-picker-option${index === active ? ' is-active' : ''}${selected ? ' is-selected' : ''}`,
      onPointerMove: () => { if (index !== active) setActive(index) },
      // Keep focus in the search field; the click chooses.
      onMouseDown: (event: { preventDefault: () => void }) => event.preventDefault(),
      onClick: () => choose(item.value),
    }
    if (item.kind === 'choice') return <div key={key} {...common} aria-label={spoken(item.choice.label, item.choice.detail)}>
      <IdentityTile>{item.choice.icon}</IdentityTile>
      <span className="account-picker-text"><span className="account-name">{item.choice.label}</span>{item.choice.detail && <small>{item.choice.detail}</small>}</span>
    </div>
    const a = item.account
    return <div key={key} {...common} title={accountTitle(a, venues.name(a.venueId))} aria-label={spoken(accountSpoken(a), a.detail, a.balance)}>
      <AccountAvatar venueId={a.venueId} size="lg" halo selected={selected} />
      <span className="account-picker-text">
        <span className="account-picker-line"><AccountName account={a} /></span>
        <span className="account-picker-line account-picker-tags"><VenueTag venueId={a.venueId} />{a.source !== undefined && <SourceChip source={a.source} />}</span>
      </span>
      {(a.balance || a.detail) && <span className="account-picker-aside">{a.balance && <b>{a.balance}</b>}{a.detail && <small>{a.detail}</small>}</span>}
    </div>
  }

  // Ungrouped choices first, then each choice group under its heading, then the accounts.
  const groups: { heading?: string | undefined; items: [Item, number][] }[] = []
  items.forEach((item, index) => {
    const heading = item.kind === 'choice' ? item.choice.group : accountsHeading
    const last = groups.at(-1)
    if (last && last.heading === heading) last.items.push([item, index]); else groups.push({ heading, items: [[item, index]] })
  })

  return <Popover.Root open={open} onOpenChange={openWith}>
    <Popover.Trigger asChild>
      <button type="button" id={id} data-value={value} data-density={density} disabled={disabled} className={`account-picker-trigger ${className}`.trim()} aria-labelledby={`${labelledBy} ${base}-value`}
        title={selectedAccount ? accountTitle(selectedAccount, venues.name(selectedAccount.venueId)) : undefined}
        onKeyDown={event => { if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); openWith(true) } }}>
        <span id={`${base}-value`} className="account-picker-value">
          <span className="sr-only">{selectedChoice ? selectedChoice.label : selectedAccount ? accountSpoken(selectedAccount, ambiguous.has(selectedAccount.id)) : empty}</span>
          {selectedChoice ? <><IdentityTile size="sm">{selectedChoice.icon}</IdentityTile><span className="account-name" aria-hidden="true">{selectedChoice.label}</span></>
            : selectedAccount ? <span className="account-picker-value-marks" aria-hidden="true"><AccountAvatar venueId={selectedAccount.venueId} size="sm" /><AccountName account={selectedAccount} />
              {showVenue && <VenueTag venueId={selectedAccount.venueId} />}
              {ambiguous.has(selectedAccount.id) && selectedAccount.name.trim() && selectedAccount.source !== undefined && <SourceChip source={selectedAccount.source} />}</span>
            : <span className="account-picker-placeholder" aria-hidden="true">{empty}</span>}
        </span>
        <ChevronDown size={14} className="account-picker-chevron" aria-hidden="true" />
      </button>
    </Popover.Trigger>
    <Popover.Portal>
      <Popover.Content className="account-picker-popover" align="start" sideOffset={6} collisionPadding={12} aria-labelledby={labelledBy}
        onOpenAutoFocus={event => { event.preventDefault(); input.current?.focus() }}>
        <div className="account-picker-search">
          <Search size={14} aria-hidden="true" />
          <input ref={input} role="combobox" aria-label={searchLabel} aria-expanded="true" aria-controls={`${base}-list`} aria-autocomplete="list"
            aria-activedescendant={items.length ? optionId(active) : undefined} autoComplete="off" spellCheck={false}
            placeholder="Search name, venue or address" value={query} onChange={event => narrow({ query: event.target.value })} onKeyDown={onSearchKey} />
        </div>
        {venueIds.length > 1 && <div className="account-picker-venues" role="group" aria-label="Filter by venue">
          <button type="button" className="account-picker-venue" aria-pressed={venue === null} onClick={() => narrow({ venue: null })}>All venues</button>
          {venueIds.map(id => <button type="button" key={id} className="account-picker-venue" aria-pressed={venue === id} title={venues.name(id)}
            onClick={() => narrow({ venue: venue === id ? null : id })}>
            <VenueLogo venueId={id} size="xs" /><span>{venues.name(id)}</span><small>{accounts.filter(a => a.venueId === id).length}</small>
          </button>)}
        </div>}
        <div className="account-picker-list" role="listbox" id={`${base}-list`} aria-labelledby={labelledBy}>
          {groups.map((group, g) => group.heading
            ? <div key={g} role="group" aria-labelledby={`${base}-group-${g}`}><div id={`${base}-group-${g}`} className="account-picker-heading">{group.heading}</div>
              {group.items.map(([item, index]) => option(item, index))}</div>
            : group.items.map(([item, index]) => option(item, index)))}
          {!items.length && <p className="account-picker-empty">No account matches</p>}
        </div>
        <span className="sr-only" role="status">{items.length === 1 ? '1 result' : `${items.length} results`}</span>
        <div className="account-picker-foot" aria-hidden="true">
          <span>{shownAccounts.length} of {accounts.length} {accounts.length === 1 ? 'account' : 'accounts'}</span>
          <span className="account-picker-keys"><kbd>↑</kbd><kbd>↓</kbd> move <kbd>Enter</kbd> choose</span>
        </div>
      </Popover.Content>
    </Popover.Portal>
  </Popover.Root>
}
