import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { accountFixture } from '@/test/workspace-fixture'
import { chooseInPicker } from '@/test/account-picker'
import { createWorkspaceApi } from '@/api/workspace'
import { DataExportSettings } from './DataExportSettings'

afterEach(() => vi.unstubAllGlobals())

describe('Data export (phase 2, 5b)', () => {
  it('downloads an account\'s executions between UTC days and refuses a reversed range', async () => {
    const user = userEvent.setup()
    const accountFacts = vi.fn().mockResolvedValue({ fileName: 'vessel-executions-x.csv', blob: new Blob(['provenance\r\n']) })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const disabled = { ...accountFixture, id: '99999999-9999-4999-8999-999999999999', name: 'Disabled', isEnabled: false }
    render(<DataExportSettings api={{ accounts: vi.fn().mockResolvedValue([accountFixture, disabled]), accountFacts }} />)
    const executions = screen.getByRole('button', { name: 'Executions CSV' })
    expect(executions).toBeDisabled()
    await chooseInPicker('Account', accountFixture.name, user)
    await user.type(screen.getByLabelText('From (UTC day)'), '2026-10-02')
    await user.type(screen.getByLabelText('To (UTC day)'), '2026-10-01')
    expect(screen.getByText('The start date is after the end date.')).toBeVisible()
    expect(executions).toBeDisabled()
    await user.clear(screen.getByLabelText('To (UTC day)'))
    await user.type(screen.getByLabelText('To (UTC day)'), '2026-10-05')
    await user.click(executions)
    expect(accountFacts).toHaveBeenLastCalledWith(accountFixture.id, 'executions', '2026-10-02', '2026-10-05')
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    click.mockRestore()
  })

  it('never reads facts in Paper mode', async () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    await expect(createWorkspaceApi('synthetic-token', undefined, 'paper').accountFacts(accountFixture.id, 'funding', null, null))
      .rejects.toMatchObject({ kind: 'unavailable' })
    expect(fetch).not.toHaveBeenCalled()
  })
})
