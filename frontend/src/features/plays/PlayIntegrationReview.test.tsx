import { useState } from 'react'
import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import { createDraft, createEntry } from './draft'
import { PlayWorkspace } from './PlayWorkspace'
import { credentialsFixture } from '@/test/identity-fixture'

it('reveals the selected entry again when its chart legend is clicked after sidebar scrolling', async () => {
  function Harness() {
    const [draft, setDraft] = useState(() => ({
      ...createDraft(),
      entries: [createEntry(0), createEntry(1)],
    }))
    return <PlayWorkspace accounts={[]} portfolios={[]} api={createWorkspaceApi(credentialsFixture)} draft={draft} onChange={setDraft} />
  }

  const user = userEvent.setup()
  render(<Harness />)
  const legend = within(screen.getByRole('group', { name: 'Planned entries' }))
  const entry = legend.getByRole('button', { name: /Entry 2/ })
  const sidebar = screen.getByTestId('entry-sidebar')
  const scroll = vi.spyOn(sidebar, 'scrollTo')

  await user.click(entry)
  expect(scroll).toHaveBeenCalled()
  expect(within(sidebar).getByRole('button', { name: /^Entry 2/ })).toHaveFocus()

  sidebar.scrollTop = 500
  scroll.mockClear()
  await user.click(entry)
  expect(scroll).toHaveBeenCalled()
  expect(within(sidebar).getByRole('button', { name: /^Entry 2/ })).toHaveFocus()
})
