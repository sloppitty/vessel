import { useState } from 'react'
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import type { ManualExecutions, ManualExecutionRecord } from '@/api/diary'
import type { SavedPlay } from '@/api/plays'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { ManualExecutionPanel } from './ManualExecutionPanel'
import { emptyManualDraft, type ManualExecutionDraft } from './manual'
import { draftFromSaved, savedState } from './saved'

const at = '2026-10-13T10:00:00Z'
const id = '00000000-0000-4000-8000-000000000001'
const play: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'planned', version: 2 } }
const record: ManualExecutionRecord = { id, role: 'entry', entryId: 'entry-1', occurredAtUtc: at, side: 'buy',
  quantity: '1.000000000000000001', price: '100.00', fee: null, feeCurrency: null, source: 'Statement', createdAtUtc: at, updatedAtUtc: at }
const ledger = (status: 'planned' | 'open' | 'closed' = 'planned', records: ManualExecutionRecord[] = []): ManualExecutions => ({
  play: { ...play, summary: { ...play.summary, status, version: status === 'planned' ? 2 : 3 } }, records,
  history: records.map(after => ({ id, executionId: after.id, before: null, after, reason: null, occurredAtUtc: at })),
  totals: { enteredQuantity: records.length ? record.quantity : '0', exitedQuantity: '0', openQuantity: records.length ? record.quantity : '0',
    grossResultUsd: '0', netResultUsd: null, feesComplete: false }, notice: 'Bounded owner facts.',
})
function Harness({ api, initial = ledger().play, enabled = true, dirty = false, onStored = vi.fn(), onForm = vi.fn() }: {
  api: WorkspaceApi; initial?: SavedPlay; enabled?: boolean; dirty?: boolean; onStored?: (play: SavedPlay) => void; onForm?: (form: ManualExecutionDraft) => void
}) {
  const [current, setCurrent] = useState(initial)
  const [form, setForm] = useState(emptyManualDraft())
  return <ManualExecutionPanel api={api} saved={savedState(current, draftFromSaved(current, []))} entries={draftFromSaved(current, []).entries}
    enabled={enabled} dirty={dirty} form={form} onChange={next => { setForm(next); onForm(next) }} onStored={next => { setCurrent(next); onStored(next); return true }} onBegin={() => vi.fn()} />
}
function client(initial = ledger()) {
  let current = initial
  const api = {
    manualExecutions: vi.fn(() => Promise.resolve(current)),
    addManualExecution: vi.fn(() => { current = ledger('open', [record]); return Promise.resolve(current) }),
    correctManualExecution: vi.fn(() => { current = ledger('closed', [record]); return Promise.resolve(current) }),
  }
  return { methods: api, api: api as unknown as WorkspaceApi }
}
async function enter(user: ReturnType<typeof userEvent.setup>, role: 'entry' | 'exit' = 'entry') {
  await user.selectOptions(screen.getByLabelText('Role'), role)
  if (role === 'entry') await user.selectOptions(screen.getByLabelText('Saved plan entry'), 'entry-1')
  fireEvent.change(screen.getByLabelText('Execution time (UTC)'), { target: { value: '2026-10-13T10:00:00' } })
  await user.selectOptions(screen.getByLabelText('Side'), role === 'entry' ? 'buy' : 'sell')
  await user.type(screen.getByLabelText('Execution quantity'), '1.000000000000000001')
  await user.type(screen.getByLabelText('Execution price'), '100.00')
  await user.type(screen.getByLabelText('Source label'), 'Statement')
}

describe('manual executions', () => {
  it('requires save then planning and does not prefill execution facts', () => {
    const { api } = client()
    render(<Harness api={api} initial={savedPlayFixture} />)
    expect(screen.getByText(/Save the Play, then plan/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Execution quantity')).not.toBeInTheDocument()
  })
  it('sends exact owner-entered strings, leaves fees unknown and displays server results and audit', async () => {
    const { api, methods } = client()
    const user = userEvent.setup()
    const stored = vi.fn()
    render(<Harness api={api} onStored={stored} />)
    expect(screen.getByLabelText('Execution quantity')).toHaveValue('')
    await enter(user)
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    expect(methods.addManualExecution).toHaveBeenCalledWith(play.summary.id, expect.objectContaining({
      expectedVersion: 2, requestId: expect.stringMatching(/^[\da-f-]{36}$/), occurredAtUtc: at, side: 'buy',
      entryId: 'entry-1', quantity: '1.000000000000000001', price: '100.00', fee: null, feeCurrency: null, source: 'Statement',
    }))
    await waitFor(() => expect(stored).toHaveBeenCalledWith(expect.objectContaining({ summary: expect.objectContaining({ status: 'open' }) })))
    expect(screen.getByText('Unavailable — costs incomplete')).toBeInTheDocument()
    await user.click(screen.getByText(/Execution audit history/))
    expect(screen.getByText(/Recorded · Initial record/)).toBeInTheDocument()
  })
  it('labels the net ledger result as the Play-local result on the manual ledger basis', async () => {
    const { api } = client(ledger('closed', [record]))
    render(<Harness api={api} initial={ledger('closed', [record]).play} />)
    const tip = await screen.findByRole('button', { name: 'About the Play-local result' })
    expect(tip.closest('dt')).toHaveTextContent(/^Net realized USD/)
    expect(tip).toHaveAccessibleDescription(/Play-local result on the manual ledger basis: sell proceeds minus buy costs/)
  })
  it('keeps failed and stale submissions intact and retries the same request identity and version', async () => {
    const { api, methods } = client()
    methods.addManualExecution.mockRejectedValueOnce(new ApiError('http', 'The Play changed.', 409))
    const form = vi.fn()
    render(<Harness api={api} onForm={form} />)
    const user = userEvent.setup()
    await enter(user)
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    await screen.findByRole('alert')
    expect(screen.getByLabelText('Execution quantity')).toHaveValue(record.quantity)
    const first = methods.addManualExecution.mock.calls[0]
    await user.click(screen.getByRole('button', { name: 'Retry same execution' }))
    expect(methods.addManualExecution.mock.calls[1]).toEqual(first)
    await waitFor(() => expect(form).toHaveBeenLastCalledWith(emptyManualDraft()))
  })
  it('submits scientific decimal strings without normalizing the entered facts', async () => {
    const { api, methods } = client()
    const user = userEvent.setup()
    render(<Harness api={api} />)
    await enter(user)
    await user.clear(screen.getByLabelText('Execution quantity'))
    await user.type(screen.getByLabelText('Execution quantity'), '1.230e-5')
    await user.clear(screen.getByLabelText('Execution price'))
    await user.type(screen.getByLabelText('Execution price'), '+1.00E+2')
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    expect(methods.addManualExecution).toHaveBeenCalledWith(play.summary.id,
      expect.objectContaining({ quantity: '1.230e-5', price: '+1.00E+2' }))
  })
  it('requires explicit side and a correction reason; preserves a rejected closed correction', async () => {
    const initial = ledger('closed', [record])
    const { api, methods } = client(initial)
    methods.correctManualExecution.mockRejectedValue(new ApiError('http', 'A Closed correction cannot reopen this Play.', 400))
    render(<Harness api={api} initial={initial.play} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: `Correct ${id}` }))
    expect(screen.getByLabelText('Role')).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    expect(methods.correctManualExecution).not.toHaveBeenCalled()
    await user.type(screen.getByLabelText('Correction reason'), 'Statement amended')
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('cannot reopen')
    expect(screen.getByLabelText('Correction reason')).toHaveValue('Statement amended')
    expect(screen.getByLabelText('Execution quantity')).toHaveValue(record.quantity)
  })
  it.each(['Z', '+00:00'])('shows and preserves all six fractional timestamp digits on a fee-only correction, suffix=%s', async suffix => {
    const precise = { ...record, occurredAtUtc: `2026-10-13T10:00:00.123456${suffix}` }
    const initial = ledger('closed', [precise])
    const { api, methods } = client(initial)
    render(<Harness api={api} initial={initial.play} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: `Correct ${id}` }))
    expect(screen.getByLabelText('Execution time (UTC)')).toHaveValue(precise.occurredAtUtc)
    expect(screen.getByLabelText('Execution time (UTC)')).toHaveAttribute('type', 'text')
    await user.type(screen.getByLabelText('Fee amount (blank = unknown)'), '0.01')
    const currency = screen.getByLabelText('Fee currency')
    const suggestions = document.getElementById(currency.getAttribute('list') ?? '')
    expect([...suggestions?.querySelectorAll('option') ?? []].map(option => option.value)).toEqual(['USDC', 'USDG', 'USDT', 'USDT0', 'USDE', 'USDe', 'USDH'])
    await user.type(currency, 'USD')
    await user.type(screen.getByLabelText('Correction reason'), 'Statement fee added')
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    expect(methods.correctManualExecution).toHaveBeenCalledWith(play.summary.id, id, expect.objectContaining({
      occurredAtUtc: precise.occurredAtUtc, role: precise.role, entryId: precise.entryId, side: precise.side,
      quantity: precise.quantity, price: precise.price, source: precise.source, fee: '0.01', feeCurrency: 'USD',
      reason: 'Statement fee added',
    }))
  })
  it('does not invent fills for a partial exit, or clear pending input before the server accepts it', async () => {
    const { api, methods } = client(ledger('open', [record]))
    let finish!: (result: ManualExecutions) => void
    methods.addManualExecution.mockImplementation(() => new Promise(resolve => { finish = resolve }))
    render(<Harness api={api} initial={ledger('open', [record]).play} />)
    const user = userEvent.setup()
    await enter(user, 'exit')
    await user.clear(screen.getByLabelText('Execution quantity'))
    await user.type(screen.getByLabelText('Execution quantity'), '0.4')
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    expect(screen.getByLabelText('Execution quantity')).toHaveValue('0.4')
    expect(screen.getByLabelText('Execution quantity')).toBeDisabled()
    const partial = { ...ledger('open', [record]), totals: { ...ledger().totals, enteredQuantity: '1', exitedQuantity: '0.4', openQuantity: '0.6', grossResultUsd: '2' } }
    methods.manualExecutions.mockResolvedValue(partial)
    await act(async () => finish(partial))
    expect(within(screen.getByLabelText('Manual execution totals')).getByText('0.6')).toBeInTheDocument()
    expect(screen.getByLabelText('Execution quantity')).toHaveValue('')
  })
  it.each([{ enabled: false }, { dirty: true }])('blocks writes for unavailable accounts or unsaved plans %j', async props => {
    const { api } = client()
    render(<Harness api={api} {...props} />)
    expect(screen.getByRole('button', { name: 'Record execution' })).toBeDisabled()
  })
})

describe('correction batch (phase 1, 1.3b)', () => {
  const exit: ManualExecutionRecord = { ...record, id: '00000000-0000-4000-8000-000000000002', role: 'exit', entryId: null, side: 'sell',
    occurredAtUtc: '2026-10-13T11:00:00Z', price: '110' }
  it('corrects an entry and an exit together with one reason, as one all-or-nothing request', async () => {
    const initial = ledger('closed', [record, exit])
    const { api } = client(initial)
    const batch = vi.fn<WorkspaceApi['correctManualExecutions']>(() => Promise.resolve(initial))
    const single = vi.fn()
    Object.assign(api, { correctManualExecutions: batch, correctManualExecution: single })
    render(<Harness api={api} initial={initial.play} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: `Correct ${id}` }))
    const quantity = screen.getByLabelText('Execution quantity')
    await user.clear(quantity); await user.type(quantity, '2')
    await user.selectOptions(screen.getByLabelText('Also correct'), exit.id)
    const exitQuantity = screen.getByLabelText('Exit · sell quantity')
    await user.clear(exitQuantity); await user.type(exitQuantity, '2')
    // Once a second execution joins, the one reason covers both and the batch is saved as a whole.
    await user.type(screen.getByLabelText('Correction reason (for every execution)'), 'Statement shows two contracts')
    await user.click(screen.getByRole('button', { name: 'Save corrections' }))
    await waitFor(() => expect(batch).toHaveBeenCalledOnce())
    expect(single).not.toHaveBeenCalled()
    const [playId, request] = batch.mock.calls[0]!
    expect(playId).toBe(play.summary.id)
    expect(request).toMatchObject({ expectedVersion: 3, reason: 'Statement shows two contracts', corrections: [
      { executionId: id, role: 'entry', entryId: 'entry-1', side: 'buy', quantity: '2', price: '100.00', source: 'Statement' },
      { executionId: exit.id, role: 'exit', entryId: null, side: 'sell', quantity: '2', price: '110', source: 'Statement' },
    ] })
  })
  it('retries a failed batch with the same request, and goes back to one correction when the other row is removed', async () => {
    const initial = ledger('closed', [record, exit])
    const { api } = client(initial)
    const batch = vi.fn<WorkspaceApi['correctManualExecutions']>().mockRejectedValueOnce(new ApiError('unavailable', 'Network lost.'))
      .mockResolvedValueOnce(initial)
    Object.assign(api, { correctManualExecutions: batch })
    render(<Harness api={api} initial={initial.play} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: `Correct ${id}` }))
    await user.selectOptions(screen.getByLabelText('Also correct'), exit.id)
    await user.type(screen.getByLabelText('Correction reason (for every execution)'), 'Statement amended')
    await user.click(screen.getByRole('button', { name: 'Save corrections' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Network lost.')
    await user.click(screen.getByRole('button', { name: 'Save corrections' }))
    await waitFor(() => expect(batch).toHaveBeenCalledTimes(2))
    expect(batch.mock.calls[1]![1]).toEqual(batch.mock.calls[0]![1])
  })
  it('offers each other execution once, and Remove returns to a single correction', async () => {
    const initial = ledger('closed', [record, exit])
    const { api } = client(initial)
    render(<Harness api={api} initial={initial.play} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: `Correct ${id}` }))
    expect(within(screen.getByLabelText('Also correct')).getAllByRole('option').map(option => option.getAttribute('value'))).toEqual(['', exit.id])
    await user.selectOptions(screen.getByLabelText('Also correct'), exit.id)
    expect(screen.queryByLabelText('Also correct')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Remove exit · sell from this correction' }))
    expect(screen.getByRole('button', { name: 'Save correction' })).toBeInTheDocument()
    expect(screen.getByLabelText('Correction reason')).toBeInTheDocument()
  })
})
