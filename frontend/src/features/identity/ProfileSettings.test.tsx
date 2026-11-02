import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Tooltip } from 'radix-ui'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { authErrorMessage, createSessionAuthApi } from '@/api/auth'
import { ApiError } from '@/api/errors'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { challengeFixture, secondWalletFixture, sessionFixture, sessionRowsFixture, walletFixture } from '@/test/identity-fixture'
import { ProfileSettings } from './ProfileSettings'
import type { SessionCheck } from './session'
import type { ShellIdentity } from './types'
import { WalletSignFlow } from './WalletSignFlow'

type Route = (init: RequestInit) => Response
let routes: Record<string, Route | Route[]>
let calls: Array<{ path: string; method: string; body: unknown }>
let announcer: () => void
let account = walletFixture.address
const signature = `0x${'cd'.repeat(65)}`
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const problem = (status: number, code: string) => json({ code }, status)
const sent = (path: string, method = 'POST') => calls.filter(call => call.path === path && call.method === method)
const atWalletLimit = () => Array.from({ length: 20 }, (_, index) => ({
  ...walletFixture, id: `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
}))

beforeEach(() => {
  calls = []
  account = walletFixture.address
  routes = {
    'GET /api/auth/wallets': () => json([walletFixture, secondWalletFixture]),
    'GET /api/auth/sessions': () => json(sessionRowsFixture),
    'POST /api/auth/step-up/challenge': init => json(challengeFixture(JSON.parse(init.body as string).address)),
    'POST /api/auth/step-up': () => new Response(null, { status: 204 }),
  }
  vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
    const method = init.method ?? 'GET'
    calls.push({ path, method, body: init.body ? JSON.parse(init.body as string) : undefined })
    const route = routes[`${method} ${path}`]
    const next = Array.isArray(route) ? route.shift() : route
    return next?.(init) ?? new Response(null, { status: 404 })
  }))
  const provider = { request: vi.fn(async ({ method }: { method: string }) => method === 'eth_requestAccounts' ? [account] : signature) }
  announcer = () => window.dispatchEvent(new CustomEvent('eip6963:announceProvider', {
    detail: { info: { uuid: 'rabby-uuid', name: 'Rabby Wallet', icon: null, rdns: 'io.rabby' }, provider },
  }))
  window.addEventListener('eip6963:requestProvider', announcer)
})
afterEach(() => window.removeEventListener('eip6963:requestProvider', announcer))

/** `check` is the server's answer to the session probe after a removal; `change` adopts another tab's session. */
function mount(check: SessionCheck = 'current') {
  const changes = new Set<() => void>()
  const identity: ShellIdentity = {
    session: sessionFixture, walletName: 'Rabby Wallet', auth: createSessionAuthApi({ userId: sessionFixture.user.id, sessionId: sessionFixture.session.id }),
    registerExitState: () => () => {}, profileRead: () => ({ generation: 0, epoch: 0 }), onProfile: vi.fn(() => true), checkSession: vi.fn(async () => check),
    onSessionChange: listener => { changes.add(listener); return () => { changes.delete(listener) } },
  }
  const change = () => act(() => { for (const listener of [...changes]) listener() })
  render(<Tooltip.Provider><NotificationProvider><ProfileSettings identity={identity} onSignOut={vi.fn()} /></NotificationProvider></Tooltip.Provider>)
  return { identity, change, user: userEvent.setup() }
}

async function confirmWithRabby(user: ReturnType<typeof userEvent.setup>) {
  const dialog = await screen.findByRole('dialog', { name: 'Confirm with a linked wallet' })
  await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
  await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
}

describe('Profile & sign-in', () => {
  it('disables linking at 20 wallets and explains the limit', async () => {
    routes['GET /api/auth/wallets'] = () => json(atWalletLimit())
    const { user } = mount()
    const link = await screen.findByRole('button', { name: 'Link wallet' })
    await waitFor(() => expect(link).toHaveAccessibleDescription(authErrorMessage('wallet-limit')))
    expect(link).toBeDisabled()
    await user.click(link)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(sent('/api/auth/step-up/challenge')).toHaveLength(0)
  })

  it('shows last-wallet before any step-up and closes the stale removal dialog', async () => {
    routes[`DELETE /api/auth/wallets/${secondWalletFixture.id}`] = () => problem(409, 'last-wallet')
    const { user } = mount()
    await user.click((await screen.findAllByRole('button', { name: /Remove/ }))[0]!)
    await user.click(await screen.findByRole('button', { name: 'Remove wallet' }))
    expect(await screen.findByText(authErrorMessage('last-wallet')!)).toBeVisible()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(sent('/api/auth/step-up/challenge')).toHaveLength(0)
    expect(sent(`/api/auth/wallets/${secondWalletFixture.id}`, 'DELETE')).toHaveLength(1)
  })

  it.each(['/api/auth/wallets/challenge', '/api/auth/wallets'])('closes linking on wallet-limit from %s without another step-up', async path => {
    routes['POST /api/auth/wallets/challenge'] = init => json(challengeFixture(JSON.parse(init.body as string).address))
    routes[`POST ${path}`] = () => {
      routes['GET /api/auth/wallets'] = () => json(atWalletLimit())
      return problem(409, 'wallet-limit')
    }
    const { user } = mount()
    await user.click(await screen.findByRole('button', { name: 'Link wallet' }))
    const dialog = await screen.findByRole('dialog', { name: 'Link another wallet' })
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
    await within(dialog).findByText(/Now sign with the wallet you want to add/)
    account = '0x19B4c7aE02F3d81e6C5a9b0D2E7f4A3c8D1e0a53'
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    if (path === '/api/auth/wallets') await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(authErrorMessage('wallet-limit')!)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Link wallet' })).toBeDisabled())
    await waitFor(() => expect(screen.getByRole('heading', { name: /Sign-in wallets/ })).toHaveFocus())
    expect(sent('/api/auth/step-up/challenge')).toHaveLength(1)
    expect(sent(path)).toHaveLength(1)
  })

  it.each(['/api/auth/step-up/challenge', '/api/auth/step-up', '/api/auth/wallets/challenge', '/api/auth/wallets'])(
    'shows Retry-After for %s without retrying or opening a nested step-up', async path => {
      routes['POST /api/auth/wallets/challenge'] = init => json(challengeFixture(JSON.parse(init.body as string).address))
      routes[`POST ${path}`] = () => new Response(JSON.stringify({ code: 'rate-limited' }), { status: 429, headers: { 'Retry-After': '600' } })
      const { user } = mount()
      await user.click(await screen.findByRole('button', { name: 'Link wallet' }))
      const dialog = await screen.findByRole('dialog', { name: 'Link another wallet' })
      await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
      if (path !== '/api/auth/step-up/challenge') await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
      if (path.startsWith('/api/auth/wallets')) {
        await within(dialog).findByText(/Now sign with the wallet you want to add/)
        account = '0x19B4c7aE02F3d81e6C5a9b0D2E7f4A3c8D1e0a53'
        await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
        if (path === '/api/auth/wallets') await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
      }
      expect(await within(dialog).findByRole('alert')).toHaveTextContent('Wait 600 seconds before trying again.')
      expect(screen.queryByRole('dialog', { name: 'Confirm with a linked wallet' })).not.toBeInTheDocument()
      expect(sent(path)).toHaveLength(1)
      await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })

  it('removes a wallet after a step-up, retrying the refused call once', async () => {
    routes[`DELETE /api/auth/wallets/${secondWalletFixture.id}`] = [() => problem(403, 'step-up-required'), () => new Response(null, { status: 204 })]
    const { user, identity } = mount()
    const removes = await screen.findAllByRole('button', { name: /Remove/ })
    await user.click(removes[0]!)
    await user.click(await screen.findByRole('button', { name: 'Remove wallet' }))
    await confirmWithRabby(user)
    await waitFor(() => expect(sent(`/api/auth/wallets/${secondWalletFixture.id}`, 'DELETE')).toHaveLength(2))
    expect(sent('/api/auth/step-up/challenge')[0]!.body).toEqual({ action: 'wallet.remove', target: secondWalletFixture.id, address: walletFixture.address })
    expect(sent('/api/auth/step-up')[0]!.body).toEqual({ challengeId: challengeFixture().challengeId, signature })
    expect(await screen.findByText('Sign-in wallet removed.')).toBeVisible()
    expect(identity.checkSession).toHaveBeenCalledOnce()
  })

  it('does not retry a second time when the server still asks for a step-up', async () => {
    routes[`DELETE /api/auth/wallets/${secondWalletFixture.id}`] = () => problem(403, 'step-up-required')
    const { user } = mount()
    await user.click((await screen.findAllByRole('button', { name: /Remove/ }))[0]!)
    await user.click(await screen.findByRole('button', { name: 'Remove wallet' }))
    await confirmWithRabby(user)
    expect(await screen.findByText('Confirm with a wallet already linked to this journal to continue.')).toBeVisible()
    expect(sent(`/api/auth/wallets/${secondWalletFixture.id}`, 'DELETE')).toHaveLength(2)
  })

  it('refuses a step-up from an address that is not linked, before asking the server', async () => {
    account = '0x19B4c7aE02F3d81e6C5a9b0D2E7f4A3c8D1e0a53'
    routes[`DELETE /api/auth/wallets/${secondWalletFixture.id}`] = () => problem(403, 'step-up-required')
    const { user } = mount()
    await user.click((await screen.findAllByRole('button', { name: /Remove/ }))[0]!)
    await user.click(await screen.findByRole('button', { name: 'Remove wallet' }))
    const dialog = await screen.findByRole('dialog', { name: 'Confirm with a linked wallet' })
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('This address isn’t linked to your journal.')
    expect(sent('/api/auth/step-up/challenge')).toHaveLength(0)
    // Trying the same address again can only fail: another wallet is the primary way on.
    expect(within(dialog).queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Other wallets' })).toHaveClass('sign-in-primary')
  })

  it('hides the label and Try again when the wallet to link is already linked', async () => {
    const { user } = mount()
    await user.click(await screen.findByRole('button', { name: 'Link wallet' }))
    const dialog = await screen.findByRole('dialog', { name: 'Link another wallet' })
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
    await within(dialog).findByText(/Now sign with the wallet you want to add/)
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('This wallet is already linked to your journal.')
    expect(within(dialog).queryByLabelText(/Label/)).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Other wallets' })).toHaveClass('sign-in-primary')
    expect(sent('/api/auth/wallets/challenge')).toHaveLength(0)
  })

  it('offers Remove only on the wallets that did not sign this session', async () => {
    mount()
    const ledger = (await screen.findByText('Ledger')).closest('li')!
    const hot = screen.getByText('Hot wallet').closest('li')!
    expect(within(ledger).getByRole('button', { name: 'Remove' })).toBeVisible()
    expect(within(hot).queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument()
    expect(within(hot).getByText('This session')).toBeVisible()
  })

  it('leaves this tab when the server says the removal ended this session, whatever the row said', async () => {
    // The row said "not this session", but another tab had moved this tab's cookie onto that wallet's session.
    routes[`DELETE /api/auth/wallets/${secondWalletFixture.id}`] = () => new Response(null, { status: 204 })
    const { user, identity } = mount('ended')
    await user.click((await screen.findAllByRole('button', { name: /Remove/ }))[0]!)
    await user.click(await screen.findByRole('button', { name: 'Remove wallet' }))
    await waitFor(() => expect(identity.checkSession).toHaveBeenCalledOnce())
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(screen.queryByText('Sign-in wallet removed.')).not.toBeInTheDocument()
    expect(sent('/api/auth/wallets', 'GET')).toHaveLength(1)
  })

  it('drops a removal prepared under the old session when this tab adopts another', async () => {
    routes[`DELETE /api/auth/wallets/${secondWalletFixture.id}`] = () => problem(403, 'step-up-required')
    const { user, change } = mount()
    await user.click((await screen.findAllByRole('button', { name: /Remove/ }))[0]!)
    await user.click(await screen.findByRole('button', { name: 'Remove wallet' }))
    await screen.findByRole('dialog', { name: 'Confirm with a linked wallet' })
    await change()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(sent('/api/auth/wallets', 'GET')).toHaveLength(2))
    expect(sent(`/api/auth/wallets/${secondWalletFixture.id}`, 'DELETE')).toHaveLength(1)
    expect(sent('/api/auth/step-up/challenge')).toHaveLength(0)
  })

  it('checks the target again at confirmation and never removes the wallet that now signs this session', async () => {
    const { user } = mount()
    await user.click(within((await screen.findByText('Ledger')).closest('li')!).getByRole('button', { name: 'Remove' }))
    const dialog = await screen.findByRole('dialog', { name: 'Remove sign-in wallet?' })
    // A reload behind the dialog (here a rename) finds that the Ledger signs this session now.
    routes['GET /api/auth/wallets'] = () => json([{ ...walletFixture, current: false }, { ...secondWalletFixture, current: true }])
    routes[`PATCH /api/auth/wallets/${walletFixture.id}`] = () => json(walletFixture)
    const hot = screen.getByText('Hot wallet').closest('li')!
    // The modal makes the page behind it inert to pointers, so the rename is driven by events.
    fireEvent.click(within(hot).getByRole('button', { name: 'Rename Hot wallet', hidden: true }))
    fireEvent.click(within(hot).getByRole('button', { name: 'Save', hidden: true }))
    await waitFor(() => expect(sent('/api/auth/wallets', 'GET')).toHaveLength(2))
    await waitFor(() => expect(within(screen.getByText('Ledger', { selector: 'strong' }).closest('li')!).getByText('This session')).toBeInTheDocument())
    await user.click(within(dialog).getByRole('button', { name: 'Remove wallet' }))
    expect(await screen.findByText('This wallet signs in this session now. Remove it from another session.')).toBeVisible()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(sent(`/api/auth/wallets/${secondWalletFixture.id}`, 'DELETE')).toHaveLength(0)
  })

  it('links a wallet: step-up first, then the new wallet signs its own message', async () => {
    routes['POST /api/auth/wallets/challenge'] = init => json(challengeFixture(JSON.parse(init.body as string).address))
    routes['POST /api/auth/wallets'] = () => json({ ...secondWalletFixture, id: '7f3a1c9e-2b4d-4e8f-a6c0-5d9e1b3f7a24', address: '0x19B4c7aE02F3d81e6C5a9b0D2E7f4A3c8D1e0a53', label: 'Phone' }, 201)
    const { user } = mount()
    await user.click(await screen.findByRole('button', { name: 'Link wallet' }))
    const dialog = await screen.findByRole('dialog', { name: 'Link another wallet' })
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
    expect(await within(dialog).findByText(/Now sign with the wallet you want to add/)).toBeVisible()
    account = '0x19B4c7aE02F3d81e6C5a9b0D2E7f4A3c8D1e0a53'
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    // The announced wallet name is the suggested label; the person's own text replaces it.
    const label = await within(dialog).findByLabelText(/Label/)
    expect(label).toHaveValue('Rabby Wallet')
    await user.clear(label)
    await user.type(label, 'Phone')
    await user.click(within(dialog).getByRole('button', { name: 'Sign with Rabby Wallet' }))
    await waitFor(() => expect(sent('/api/auth/wallets')).toHaveLength(1))
    expect(sent('/api/auth/step-up/challenge')[0]!.body).toEqual({ action: 'wallet.link', address: walletFixture.address })
    expect(sent('/api/auth/wallets/challenge')[0]!.body).toEqual({ address: account })
    expect(sent('/api/auth/wallets')[0]!.body).toEqual({ challengeId: challengeFixture().challengeId, signature, label: 'Phone' })
  })

  it('shows the server\'s label refusal on the label field', async () => {
    routes['POST /api/auth/wallets/challenge'] = init => json(challengeFixture(JSON.parse(init.body as string).address))
    routes['POST /api/auth/wallets'] = () => problem(400, 'label-invalid')
    const { user } = mount()
    await user.click(await screen.findByRole('button', { name: 'Link wallet' }))
    const dialog = await screen.findByRole('dialog', { name: 'Link another wallet' })
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
    await within(dialog).findByText(/Now sign with the wallet you want to add/)
    account = '0x19B4c7aE02F3d81e6C5a9b0D2E7f4A3c8D1e0a53'
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    await user.clear(await within(dialog).findByLabelText(/Label/))
    await user.type(within(dialog).getByLabelText(/Label/), 'Desk')
    await user.click(within(dialog).getByRole('button', { name: 'Sign with Rabby Wallet' }))
    await waitFor(() => expect(within(dialog).getByLabelText(/Label/)).toHaveAttribute('aria-invalid', 'true'))
    expect(within(dialog).getByLabelText(/Label/)).toHaveAccessibleDescription('Use at most 40 printable characters for the label.')
  })

  it('refuses a label the server would refuse before asking it, and sends the trimmed form', async () => {
    routes['PATCH /api/auth/wallets/' + secondWalletFixture.id] = () => json({ ...secondWalletFixture, label: 'Cold' })
    const { user } = mount()
    await user.click(await screen.findByRole('button', { name: 'Rename Ledger' }))
    const input = screen.getByLabelText(`Label for ${secondWalletFixture.address}`)
    const save = within(input.closest('form')!).getByRole('button', { name: 'Save' })
    await user.clear(input)
    await user.type(input, 'Co\u200bld')
    await user.click(save)
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(sent(`/api/auth/wallets/${secondWalletFixture.id}`, 'PATCH')).toHaveLength(0)
    await user.clear(input)
    await user.type(input, '  Cold ')
    await user.click(save)
    await waitFor(() => expect(sent(`/api/auth/wallets/${secondWalletFixture.id}`, 'PATCH')[0]!.body).toEqual({ label: 'Cold' }))
  })

  describe('focus', () => {
    it('returns to Link wallet when its dialog closes with Escape', async () => {
      const { user } = mount()
      const link = await screen.findByRole('button', { name: 'Link wallet' })
      await waitFor(() => expect(link).toBeEnabled())
      await user.click(link)
      await screen.findByRole('dialog', { name: 'Link another wallet' })
      await user.keyboard('{Escape}')
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      await waitFor(() => expect(link).toHaveFocus())
    })

    it.each<[string, (user: ReturnType<typeof userEvent.setup>, form: HTMLElement) => Promise<void>]>([
      ['Cancel', (user, form) => user.click(within(form).getByRole('button', { name: 'Cancel' }))],
      ['Escape', user => user.keyboard('{Escape}')],
      ['a successful save', (user, form) => user.click(within(form).getByRole('button', { name: 'Save' }))],
    ])('returns to the row\'s Rename control after leaving the label form by %s', async (_, leave) => {
      routes[`PATCH /api/auth/wallets/${secondWalletFixture.id}`] = () => json(secondWalletFixture)
      const { user } = mount()
      await user.click(await screen.findByRole('button', { name: 'Rename Ledger' }))
      const input = screen.getByRole('textbox', { name: `Label for ${secondWalletFixture.address}` })
      expect(input).toHaveFocus()
      await leave(user, input.closest('form')!)
      await waitFor(() => expect(screen.getByRole('button', { name: 'Rename Ledger' })).toHaveFocus())
      expect(screen.queryByRole('textbox', { name: `Label for ${secondWalletFixture.address}` })).not.toBeInTheDocument()
    })

    it('returns to the row\'s Remove button when the removal dialog is cancelled', async () => {
      const { user } = mount()
      const remove = (await screen.findAllByRole('button', { name: /Remove/ }))[0]!
      await user.click(remove)
      await user.click(await screen.findByRole('button', { name: 'Cancel' }))
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      await waitFor(() => expect(remove).toHaveFocus())
    })

    it('returns from a nested step-up to the dialog underneath', async () => {
      routes[`DELETE /api/auth/wallets/${secondWalletFixture.id}`] = () => problem(403, 'step-up-required')
      const { user } = mount()
      await user.click((await screen.findAllByRole('button', { name: /Remove/ }))[0]!)
      const removal = await screen.findByRole('dialog', { name: 'Remove sign-in wallet?' })
      const confirm = within(removal).getByRole('button', { name: 'Remove wallet' })
      await user.click(confirm)
      await screen.findByRole('dialog', { name: 'Confirm with a linked wallet' })
      await user.keyboard('{Escape}')
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Confirm with a linked wallet' })).not.toBeInTheDocument())
      await waitFor(() => expect(removal.contains(document.activeElement)).toBe(true))
    })
  })

  describe('when the step-up grant lapses before the new wallet signs', () => {
    const phone = '0x19B4c7aE02F3d81e6C5a9b0D2E7f4A3c8D1e0a53'
    const linked = { ...secondWalletFixture, id: '7f3a1c9e-2b4d-4e8f-a6c0-5d9e1b3f7a24', address: phone, label: null }
    async function linkUntilRefused(user: ReturnType<typeof userEvent.setup>) {
      await user.click(await screen.findByRole('button', { name: 'Link wallet' }))
      const dialog = await screen.findByRole('dialog', { name: 'Link another wallet' })
      await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
      await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
      await within(dialog).findByText(/Now sign with the wallet you want to add/)
      account = phone
      await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
      await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
      // 403 step-up-required at completion: confirm again with the linked wallet.
      await screen.findByRole('dialog', { name: 'Confirm with a linked wallet' })
      account = walletFixture.address
      await confirmWithRabby(user)
      account = phone
      return dialog
    }

    it('confirms again, then signs a fresh link message instead of the spent one', async () => {
      let issued = 0
      routes['POST /api/auth/wallets/challenge'] = init => json({ ...challengeFixture(JSON.parse(init.body as string).address), challengeId: issued++ ? 'e2c4b6a8-1d3f-4a5b-9c7d-0e1f2a3b4c5d' : challengeFixture().challengeId })
      routes['POST /api/auth/wallets'] = [() => problem(403, 'step-up-required'), () => json(linked, 201)]
      const { user } = mount()
      const dialog = await linkUntilRefused(user)
      // The nested step-up hands focus back to the link dialog.
      await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true))
      await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
      await waitFor(() => expect(sent('/api/auth/wallets')).toHaveLength(2))
      expect(sent('/api/auth/wallets/challenge')).toHaveLength(2)
      expect(sent('/api/auth/wallets').map(call => (call.body as { challengeId: string }).challengeId))
        .toEqual([challengeFixture().challengeId, 'e2c4b6a8-1d3f-4a5b-9c7d-0e1f2a3b4c5d'])
      expect(sent('/api/auth/step-up/challenge')).toHaveLength(2)
      expect(await screen.findByText('Wallet linked. It can now sign in to this journal.')).toBeVisible()
    })

    it('steps up only once per attempt', async () => {
      routes['POST /api/auth/wallets/challenge'] = init => json(challengeFixture(JSON.parse(init.body as string).address))
      routes['POST /api/auth/wallets'] = () => problem(403, 'step-up-required')
      const { user } = mount()
      const dialog = await linkUntilRefused(user)
      await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
      expect(await within(dialog).findByRole('alert')).toHaveTextContent('Confirm with a wallet already linked to this journal to continue.')
      expect(sent('/api/auth/wallets')).toHaveLength(2)
      expect(sent('/api/auth/step-up/challenge')).toHaveLength(2)
      expect(screen.queryByRole('dialog', { name: 'Confirm with a linked wallet' })).not.toBeInTheDocument()
    })
  })
})

describe('wallet signing flow', () => {
  it('stops at unmount, so a late step-up-required answer opens no step-up and completes nothing', async () => {
    let refuse!: (cause: unknown) => void
    const complete = vi.fn(() => new Promise<void>((_resolve, reject) => { refuse = reject }))
    const onStepUp = vi.fn().mockResolvedValue(true)
    const onDone = vi.fn()
    const user = userEvent.setup()
    const view = render(<Tooltip.Provider><WalletSignFlow purpose="Link wallet" requestChallenge={async address => challengeFixture(address)}
      complete={complete} onStepUp={onStepUp} onDone={onDone} onCancel={vi.fn()} /></Tooltip.Provider>)
    await user.click(await screen.findByRole('button', { name: /Rabby Wallet/ }))
    await user.click(await screen.findByRole('button', { name: 'Sign with Rabby Wallet' }))
    await waitFor(() => expect(complete).toHaveBeenCalledOnce())
    view.unmount()
    refuse(new ApiError('http', 'Confirm first.', 403, undefined, 'step-up-required'))
    await new Promise(resolve => setTimeout(resolve, 25))
    expect(onStepUp).not.toHaveBeenCalled()
    expect(onDone).not.toHaveBeenCalled()
  })
})

describe('device pairing in Sessions', () => {
  const pairedRow = { ...sessionRowsFixture[1]!, id: '5a3f1c88-2b7d-4e90-8c14-d6e0b9a47f21', userAgent: 'Safari on iPhone', source: 'pairing' as const, remember: true }

  it('labels a paired device as such, with a sign-out that says the same', async () => {
    routes['GET /api/auth/sessions'] = () => json([...sessionRowsFixture, pairedRow])
    mount()
    expect(await screen.findByText('Paired · Safari on iPhone')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Sign out Paired · Safari on iPhone session' })).toBeVisible()
    // A wallet session keeps its plain label.
    expect(screen.getByText('Safari on iOS')).toBeVisible()
  })

  it('offers Connect a phone, which opens the dialog and sends nothing before the wallet signs', async () => {
    const { user } = mount()
    const button = await screen.findByRole('button', { name: 'Connect a phone' })
    await waitFor(() => expect(button).toBeEnabled())
    await user.click(button)
    // Under test the page is on a loopback address, so the dialog explains instead of showing a QR.
    const dialog = await screen.findByRole('dialog', { name: 'Connect a phone' })
    expect(within(dialog).getByRole('note')).toHaveTextContent('A phone can’t open this address')
    expect(sent('/api/auth/pairings')).toHaveLength(0)
    await user.click(within(dialog).getAllByRole('button', { name: 'Close' })[0]!)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('does not offer Connect a phone on a paired device, which cannot sign for the step-up', async () => {
    routes['GET /api/auth/sessions'] = () => json([{ ...sessionRowsFixture[0]!, source: 'pairing' }, sessionRowsFixture[1]])
    mount()
    expect(await screen.findByText('Paired · Firefox on Linux')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Connect a phone' })).not.toBeInTheDocument()
  })
})
