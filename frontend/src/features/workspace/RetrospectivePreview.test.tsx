import { act, render, screen, within, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import type { RetrospectivePreview } from '@/api/retrospective'
import { ApiError } from '@/api/errors'
import { accountFixture } from '@/test/workspace-fixture'
import { activityPage } from '@/test/activity-fixture'
import { retrospectiveFixture, retrospectiveSavedFixture as savedPlayFixture } from '@/test/retrospective.fixtures'
import { ActivityOperations } from './ActivityOperations'
import { duration, operationDates } from './activity-format'
import { amount, money } from '@/lib/format'

function setup(preview: RetrospectivePreview = retrospectiveFixture, overrides: Partial<WorkspaceApi> = {}) {
  const api = {
    activity: vi.fn().mockResolvedValue(activityPage([preview.operation], { scopeNote: 'Synthetic retained facts only.' })),
    activityFills: vi.fn().mockResolvedValue({ operationId: preview.operation.id, fills: preview.operation.fills,
      omittedFillCount: preview.operation.omittedFillCount, fundingPayments: [], omittedFundingPaymentCount: 0 }),
    retrospectivePreview: vi.fn().mockResolvedValue(preview),
    createRetrospectivePlay: vi.fn().mockResolvedValue(savedPlayFixture), ...overrides,
  } as unknown as WorkspaceApi
  const open = vi.fn().mockResolvedValue(undefined), created = vi.fn()
  const view = render(<ActivityOperations api={api} accounts={[{ ...accountFixture, venueId: 'hyperliquid' }]} onOpenPlay={open} onCreated={created} />)
  return { api, open, created, view, user: userEvent.setup() }
}
async function inspect() {
  await userEvent.click(await screen.findByRole('button', { name: 'Expand BTC operation' }))
  await userEvent.click(screen.getByRole('button', { name: 'Inspect BTC retrospective eligibility' }))
  return screen.getByRole('dialog')
}
describe('Activity retrospective inspection', () => {
  it('requests authoritative preview, shows exact originals, and confirms only identity/fingerprint and owner text', async () => {
    const { api, open, created, user } = setup()
    const dialog = await inspect()
    await within(dialog).findByText('Original imported fills and associations (2)', { selector: 'summary' })
    expect(api.retrospectivePreview).toHaveBeenCalledWith(accountFixture.id, retrospectiveFixture.operation.id, expect.any(AbortSignal))
    expect(within(dialog).getByText('100.123456789123456789', { exact: false })).toBeInTheDocument()
    expect(within(dialog).getByText(retrospectiveFixture.operation.fills[0]!.fill.id)).toBeInTheDocument()
    expect(within(dialog).getByText('Source fill 1')).toBeInTheDocument()
    expect(within(dialog).getAllByText('Unassigned')).toHaveLength(2)
    await user.clear(within(dialog).getByLabelText('Retrospective title')); await user.type(within(dialog).getByLabelText('Retrospective title'), 'After-the-fact record')
    await user.type(within(dialog).getByLabelText('Review note, optional'), 'My review')
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    await waitFor(() => expect(open).toHaveBeenCalledWith(savedPlayFixture.summary.id, expect.any(AbortSignal)))
    expect(created).toHaveBeenCalledOnce()
    expect(api.createRetrospectivePlay).toHaveBeenCalledWith({
      requestId: expect.stringMatching(/^[\da-f-]{36}$/), accountId: accountFixture.id,
      operationId: retrospectiveFixture.operation.id, expectedFingerprint: retrospectiveFixture.fingerprint,
      title: 'After-the-fact record', review: 'My review',
    }, expect.any(AbortSignal))
    expect(Object.keys(vi.mocked(api.createRetrospectivePlay!).mock.calls[0]![0]).sort()).toEqual([
      'accountId', 'expectedFingerprint', 'operationId', 'requestId', 'review', 'title',
    ])
  })
  it('leads with the trade in plain terms, keeps raw records collapsed and saves the default title when untouched (F-06)', async () => {
    const { api, user } = setup()
    const dialog = await inspect()
    const facts = await within(dialog).findByLabelText('Trade summary')
    const op = retrospectiveFixture.operation
    for (const [label, value] of [['Quantity', amount(op.totalEnteredQuantity!)], ['Average entry', money(op.averageEntryPrice)],
      ['Average exit', money(op.averageExitPrice)], ['Net execution result', money(op.netResultUsd)]] as const)
      expect(within(facts).getByText(label).nextElementSibling).toHaveTextContent(value)
    expect(within(facts).getByText('Dates', { exact: false }).nextElementSibling)
      .toHaveTextContent(`${operationDates(op)} · ${duration(op.openedAtUtc!, op.closedAtUtc!)}`)
    // Fill IDs and transaction hashes come after the summary, inside a closed disclosure.
    const records = within(dialog).getByText(retrospectiveFixture.operation.fills[0]!.fill.id).closest('details')!
    expect(records).not.toHaveAttribute('open')
    expect(facts.compareDocumentPosition(records) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()

    expect(within(dialog).getByLabelText('Retrospective title')).toHaveValue('BTC Long · 12 Oct 2026')
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    await waitFor(() => expect(api.createRetrospectivePlay).toHaveBeenCalledOnce())
    expect(vi.mocked(api.createRetrospectivePlay!).mock.calls[0]![0].title).toBe('BTC Long · 12 Oct 2026')
  })
  it('saves the default title when the owner clears the field', async () => {
    const { api, user } = setup()
    const dialog = await inspect()
    await user.clear(await within(dialog).findByLabelText('Retrospective title'))
    expect(within(dialog).getByLabelText('Retrospective title')).toHaveAttribute('placeholder', 'BTC Long · 12 Oct 2026')
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    await waitFor(() => expect(api.createRetrospectivePlay).toHaveBeenCalledOnce())
    expect(vi.mocked(api.createRetrospectivePlay!).mock.calls[0]![0].title).toBe('BTC Long · 12 Oct 2026')
  })
  it.each(['mixed', 'linked', 'flip', 'incomplete'] as const)('keeps %s records inspectable without offering creation', async kind => {
    const preview: RetrospectivePreview = { ...retrospectiveFixture, eligible: false, reasons: ['Order 2 cannot be attributed to one role.'],
      operation: { ...retrospectiveFixture.operation,
        ...(kind === 'linked' ? { playAssociation: 'linked' as const, playId: savedPlayFixture.summary.id } : {}),
        ...(kind === 'mixed' ? { playAssociation: 'mixed' as const } : {}),
        ...(kind === 'incomplete' ? { status: 'incomplete' as const, omittedFillCount: 3, fillCount: 5 } : {}),
        ...(kind === 'flip' ? { fills: retrospectiveFixture.operation.fills.map(f => ({ ...f, fill: { ...f.fill, positionEffect: 'flip' } })) } : {}),
      } }
    const { api, open, user } = setup(preview)
    const dialog = await inspect()
    expect(await within(dialog).findByText('Order 2 cannot be attributed to one role.')).toBeInTheDocument()
    expect(within(dialog).getByText('Original imported fills and associations (2)', { selector: 'summary' })).toBeInTheDocument()
    expect(within(dialog).queryByLabelText('Retrospective title')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Confirm retrospective Play' })).not.toBeInTheDocument()
    expect(api.createRetrospectivePlay).not.toHaveBeenCalled()
    if (kind === 'linked') {
      await user.click(within(dialog).getByRole('button', { name: 'Open linked Play' }))
      expect(open).toHaveBeenCalledWith(savedPlayFixture.summary.id, expect.any(AbortSignal))
    }
  })
  it('holds the same request ID and payload after uncertain failure, including exact retry text', async () => {
    const create = vi.fn().mockRejectedValueOnce(new Error('raw transport secret')).mockResolvedValue(savedPlayFixture)
    const { open, user } = setup(retrospectiveFixture, { createRetrospectivePlay: create })
    const dialog = await inspect()
    await user.clear(await within(dialog).findByLabelText('Retrospective title')); await user.type(within(dialog).getByLabelText('Retrospective title'), 'Retry safely')
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Retry the same request')
    expect(screen.queryByText('raw transport secret')).not.toBeInTheDocument()
    expect(within(dialog).getByLabelText('Retrospective title')).toBeDisabled()
    await user.click(within(dialog).getByRole('button', { name: 'Retry same creation' }))
    await waitFor(() => expect(open).toHaveBeenCalled())
    expect(create.mock.calls[0]![0]).toEqual(create.mock.calls[1]![0])
  })
  it('retries a failed preview without presenting local operation facts as an authoritative preview', async () => {
    const preview = vi.fn().mockRejectedValueOnce(new Error('private transport detail')).mockResolvedValue(retrospectiveFixture)
    const { user } = setup(retrospectiveFixture, { retrospectivePreview: preview })
    const dialog = await inspect()
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('The operation preview could not be loaded.')
    expect(within(dialog).queryByText('Original imported fills and associations (2)', { selector: 'summary' })).not.toBeInTheDocument()
    expect(within(dialog).queryByLabelText('Retrospective title')).not.toBeInTheDocument()
    expect(screen.queryByText('private transport detail')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Retry preview' }))
    expect(await within(dialog).findByLabelText('Retrospective title')).toBeEnabled()
  })
  it('recovers failed saved-Play navigation without creating the retrospective record again', async () => {
    const { api, open, user } = setup()
    open.mockRejectedValueOnce(new Error('private loader failure'))
    const dialog = await inspect()
    await user.clear(await within(dialog).findByLabelText('Retrospective title')); await user.type(within(dialog).getByLabelText('Retrospective title'), 'Created once')
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('no new creation is needed')
    expect(within(dialog).getByText('Retrospective Play saved. Its original facts remain intact.')).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Open saved Play' }))
    expect(open).toHaveBeenCalledTimes(2)
    expect(api.createRetrospectivePlay).toHaveBeenCalledOnce()
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument()
  })
  it('requires a fresh preview after conflict, then sends its fingerprint and a new stable key', async () => {
    const create = vi.fn().mockRejectedValueOnce(new ApiError('http', 'stale', 409)).mockResolvedValue(savedPlayFixture)
    const preview = vi.fn().mockResolvedValueOnce(retrospectiveFixture).mockResolvedValue({ ...retrospectiveFixture, fingerprint: 'verified-facts-v2' })
    const { user } = setup(retrospectiveFixture, { createRetrospectivePlay: create, retrospectivePreview: preview })
    const dialog = await inspect()
    await user.clear(await within(dialog).findByLabelText('Retrospective title')); await user.type(within(dialog).getByLabelText('Retrospective title'), 'Conflict inspection')
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Refresh the preview')
    expect(within(dialog).getByRole('button', { name: 'Retry same creation' })).toBeDisabled()
    await user.click(within(dialog).getByRole('button', { name: 'Refresh preview' }))
    await within(dialog).findByRole('button', { name: 'Confirm retrospective Play' })
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2))
    expect(create.mock.calls[1]![0].expectedFingerprint).toBe('verified-facts-v2')
    expect(create.mock.calls[1]![0].requestId).not.toBe(create.mock.calls[0]![0].requestId)
  })
  it('cancels a pending preview on dialog close and restores the keyboard trigger', async () => {
    let finish!: (value: RetrospectivePreview) => void
    const preview = vi.fn().mockImplementation(() => new Promise<RetrospectivePreview>(resolve => { finish = resolve }))
    const { user } = setup(retrospectiveFixture, { retrospectivePreview: preview })
    await inspect()
    await user.keyboard('{Escape}')
    expect((preview.mock.calls[0]![2] as AbortSignal).aborted).toBe(true)
    expect(screen.getByRole('button', { name: 'Inspect BTC retrospective eligibility' })).toHaveFocus()
    await act(async () => finish(retrospectiveFixture))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it.each(['close', 'unmount', 'API replacement', 'disabled account'])('aborts a pending creation on %s and ignores late success', async action => {
    let finish!: (value: typeof savedPlayFixture) => void
    const create = vi.fn().mockImplementation(() => new Promise<typeof savedPlayFixture>(resolve => { finish = resolve }))
    const { api, view, open, created, user } = setup(retrospectiveFixture, { createRetrospectivePlay: create })
    const dialog = await inspect()
    await user.clear(await within(dialog).findByLabelText('Retrospective title')); await user.type(within(dialog).getByLabelText('Retrospective title'), 'Cancelled wait')
    await user.click(within(dialog).getByRole('button', { name: 'Confirm retrospective Play' }))
    if (action === 'close') await user.keyboard('{Escape}')
    else if (action === 'unmount') view.unmount()
    else if (action === 'disabled account') view.rerender(<ActivityOperations api={api} accounts={[{ ...accountFixture, isEnabled: false }]} onOpenPlay={open} onCreated={created} />)
    else view.rerender(<ActivityOperations api={{ ...api }} accounts={[accountFixture]} onOpenPlay={open} onCreated={created} />)
    expect((create.mock.calls[0]![1] as AbortSignal).aborted).toBe(true)
    await act(async () => finish(savedPlayFixture))
    expect(open).not.toHaveBeenCalled()
    expect(created).not.toHaveBeenCalled()
  })
})
