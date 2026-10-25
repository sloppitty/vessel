import { useState } from 'react'
import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { expect, it } from 'vitest'
import { createWorkspaceApi, type BrokerAccount } from '@/api/workspace'
import { accountFixture, portfolioFixture } from '@/test/workspace-fixture'
import { createDraft } from './draft'
import { PlayWorkspace } from './PlayWorkspace'
import { accountPicker, chooseInPicker } from '@/test/account-picker'
import { credentialsFixture } from '@/test/identity-fixture'

it('does not restore an obsolete portfolio filter when changing the account after its portfolio changes', async () => {
  const api = createWorkspaceApi(credentialsFixture)
  function Harness({ accounts }: { accounts: BrokerAccount[] }) {
    const [draft, setDraft] = useState(() => ({
      ...createDraft(), accountId: accountFixture.id, instrument: 'Manual contract',
    }))
    return <PlayWorkspace accounts={accounts} portfolios={[portfolioFixture]} api={api}
      draft={draft} onChange={setDraft} />
  }

  const user = userEvent.setup()
  const other = { ...accountFixture, id: 'bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee', name: 'Other account' }
  const view = render(<Harness accounts={[accountFixture, other]} />)
  await user.selectOptions(screen.getByRole('combobox', { name: 'Portfolio' }), portfolioFixture.id)

  view.rerender(<Harness accounts={[{ ...accountFixture, portfolioId: null }, other]} />)
  expect(screen.getByRole('combobox', { name: 'Portfolio' })).toHaveValue('')
  expect(accountPicker('Account')).toHaveAttribute('data-value', accountFixture.id)
  expect(screen.getByRole('textbox', { name: 'Perpetual instrument' })).toHaveValue('Manual contract')

  await chooseInPicker('Account', other.name, user)
  expect(accountPicker('Account')).toHaveAttribute('data-value', other.id)
  expect(screen.getByRole('textbox', { name: 'Perpetual instrument' })).toHaveValue('')
  expect(screen.getByRole('combobox', { name: 'Portfolio' })).toHaveValue('')
  await user.click(accountPicker('Account'))
  expect(screen.getByRole('option', { name: /^Main account,/ })).toBeInTheDocument()
})
