import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { ReportingSettings } from './ReportingSettings'

describe('Reporting week settings (Phase 2, item 2)', () => {
  it('starts from the server default, saves a zone and week start, and shows the server refusing a zone', async () => {
    const user = userEvent.setup()
    const updateReportingSettings = vi.fn()
      .mockRejectedValueOnce(new ApiError('http', 'Choose an IANA time zone such as Europe/Madrid or UTC.', 400))
      .mockResolvedValueOnce({ timeZone: 'Europe/Madrid', weekStart: 'sunday', isDefault: false })
    const api = { reportingSettings: vi.fn().mockResolvedValue({ timeZone: 'UTC', weekStart: 'monday', isDefault: true }), updateReportingSettings }
    render(<ReportingSettings api={api} />)
    const zone = await screen.findByRole('combobox', { name: 'Reporting time zone (IANA)' })
    expect(zone).toHaveValue('UTC')
    expect(screen.getByText(/Not set yet: weeks use UTC and start on Monday/)).toBeInTheDocument()
    await user.clear(zone)
    await user.type(zone, 'Europe/Nowhere')
    await user.click(screen.getByRole('button', { name: 'Save reporting week' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose an IANA time zone')
    await user.clear(zone)
    await user.type(zone, 'Europe/Madrid')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Weeks start on' }), 'sunday')
    await user.click(screen.getByRole('button', { name: 'Save reporting week' }))
    expect(updateReportingSettings).toHaveBeenLastCalledWith({ timeZone: 'Europe/Madrid', weekStart: 'sunday' })
    expect(await screen.findByRole('status')).toHaveTextContent('Reporting setting saved.')
    expect(screen.queryByText(/Not set yet/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save reporting week' })).toBeDisabled()
  })
})
