import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { PlayExecution } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { monitorFixture } from '@/test/monitor-fixture'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { usePlayExecution } from './usePlayExecution'

/*
 * Every ordering of starts and completions of one or two execution reads and one mutation, through the real hook
 * (fourth Sol review of 0.5b). The server is modelled pessimistically. A read is started because the facts changed (a
 * venue import, say), so the state moves on as it starts, and it answers with the state as of its start. A mutation
 * commits when it completes, also when it fails. Each answer carries that state's version in `notice`.
 *
 * (a) Once the hook says the execution is fresh, it is never older than any state it has heard of: the commit of every
 *     completed mutation and the start of every completed read, failed ones included. It is never fresh while a
 *     mutation is in flight. A read that is only pending does not count, so routine refreshes do not flicker.
 * (b) It is fresh at the end whenever the last request to complete (and not be aborted by a newer read) succeeded and
 *     started after the last mutation completed.
 * (c) The execution shown never goes back to an older state.
 */

type Step = 'R1.start' | 'R1.end' | 'R2.start' | 'R2.end' | 'M.start' | 'M.end'
interface Outcome { r1: boolean; r2: boolean; mutation: boolean; returns: boolean; followUp: 'pending' | 'ok' | 'fail' }

const base: PlayExecution = {
  playId: savedPlayFixture.summary.id, status: 'open', tracked: true, checkedAtUtc: null, reason: null,
  totals: { enteredQuantity: '0', exitedQuantity: '0', openQuantity: '0', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [] },
  entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: 'v0', issues: [], monitor: monitorFixture, discrepancies: [], watches: [],
}
const at = (version: number): PlayExecution => ({ ...base, notice: `v${version}` })
const versionOf = (execution: PlayExecution) => Number(execution.notice.slice(1))

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

/** Orderings of the steps in which each request starts before it ends and R1 starts before R2. */
function orderings(steps: Step[]): Step[][] {
  const valid = (order: Step[]) => (['R1', 'R2', 'M'] as const).every(name =>
    !order.includes(`${name}.start`) || order.indexOf(`${name}.start`) < order.indexOf(`${name}.end`)) &&
    (!order.includes('R2.start') || order.indexOf('R1.start') < order.indexOf('R2.start'))
  const all: Step[][] = []
  const permute = (rest: Step[], prefix: Step[]) => {
    if (!rest.length) { if (valid(prefix)) all.push(prefix); return }
    rest.forEach((step, index) => permute([...rest.slice(0, index), ...rest.slice(index + 1)], [...prefix, step]))
  }
  permute(steps, [])
  return all
}

const outcomes = (reads: 1 | 2): Outcome[] => [true, false].flatMap(r1 => (reads === 2 ? [true, false] : [true]).flatMap(r2 =>
  [true, false].flatMap(mutation => [true, false].flatMap(returns => (['pending', 'ok', 'fail'] as const).map(followUp =>
    ({ r1, r2, mutation, returns, followUp }))))))

interface Read { answer: ReturnType<typeof deferred<PlayExecution>>; version: number; signal: AbortSignal }
interface Completion { kind: 'read' | 'mutation'; ok: boolean; afterMutation: boolean; aborted: boolean }

async function run(order: Step[], outcome: Outcome) {
  let server = 0
  let mutationDone = false
  let mutating = false
  const reads: Read[] = []
  const playExecution = vi.fn((_id: string, signal: AbortSignal) => {
    const answer = deferred<PlayExecution>()
    reads.push({ answer, version: server, signal })
    return answer.promise
  })
  const change = deferred<PlayExecution | void>()
  const api = { playExecution, linkOrder: vi.fn(() => change.promise), dismissPlayDiscrepancy: vi.fn(() => change.promise) } as unknown as WorkspaceApi
  const view = renderHook(() => usePlayExecution(api, base.playId, 'open'))
  await act(async () => reads[0]!.answer.resolve(at(0)))
  expect(view.result.current.stale).toBe(false)
  const started: Partial<Record<'R1' | 'R2', { read: Read; afterMutation: boolean }>> = {}
  const completions: Completion[] = []
  let pendingChange: Promise<unknown> | null = null
  // The newest state the hook has heard of, and the version it showed last.
  let known = 0
  let shown = 0
  const check = (label: string) => {
    const { stale, execution } = view.result.current
    if (mutating) expect(stale, `${label}: fresh while a mutation is in flight`).toBe(true)
    if (!stale && execution) expect(versionOf(execution), `${label}: fresh but older than known facts`).toBeGreaterThanOrEqual(known)
    if (execution) {
      expect(versionOf(execution), `${label}: went back to an older execution`).toBeGreaterThanOrEqual(shown)
      shown = versionOf(execution)
    }
  }
  const finish = async (read: Read, ok: boolean, afterMutation: boolean) => {
    completions.push({ kind: 'read', ok, afterMutation, aborted: read.signal.aborted })
    if (!read.signal.aborted) known = Math.max(known, read.version)
    await act(async () => ok ? read.answer.resolve(at(read.version)) : read.answer.reject(new ApiError('unavailable', 'Read failed.')))
  }
  for (const step of order) {
    const [name, phase] = step.split('.') as ['R1' | 'R2' | 'M', 'start' | 'end']
    if (name === 'M' && phase === 'start') {
      mutating = true
      act(() => {
        pendingChange = (outcome.returns ? view.result.current.link({ orderId: 'order-1', role: 'entry' })
          : view.result.current.dismiss('discrepancy-1')).catch(() => undefined)
      })
    } else if (name === 'M') {
      server++
      known = server
      mutating = false
      mutationDone = true
      completions.push({ kind: 'mutation', ok: outcome.mutation, afterMutation: false, aborted: false })
      await act(async () => {
        if (outcome.mutation) change.resolve(outcome.returns ? at(server) : undefined)
        else change.reject(new ApiError('http', 'Change refused.', 409))
        await pendingChange
      })
    } else if (phase === 'start') {
      server++
      act(() => view.result.current.reload())
      started[name] = { read: reads.at(-1)!, afterMutation: mutationDone }
    } else {
      await finish(started[name]!.read, name === 'R1' ? outcome.r1 : outcome.r2, started[name]!.afterMutation)
    }
    check(`${order.join(' ')} after ${step}`)
  }
  // The follow-up read the hook starts once the mutation completes.
  const followUp = reads.find((read, index) => index > 0 && !Object.values(started).some(entry => entry?.read === read))
  if (followUp && outcome.followUp !== 'pending') await finish(followUp, outcome.followUp === 'ok', true)
  check(`${order.join(' ')} at the end`)
  const last = completions.filter(completion => !completion.aborted).at(-1)
  if (last?.kind === 'read' && last.ok && last.afterMutation)
    expect(view.result.current.stale, `${order.join(' ')}: stale after a successful read that started after the mutation`).toBe(false)
  view.unmount()
}

describe('execution freshness over every interleaving of reads and one mutation', () => {
  const one = orderings(['R1.start', 'R1.end', 'M.start', 'M.end'])
  const two = orderings(['R1.start', 'R1.end', 'R2.start', 'R2.end', 'M.start', 'M.end'])

  it('enumerates every ordering', () => {
    expect([one.length, two.length]).toEqual([6, 45])
  })

  it.each(one.map(order => [order.join(' ')]))('one read and one mutation: %s', async label => {
    for (const outcome of outcomes(1)) await run(label.split(' ') as Step[], outcome)
  })

  it.each(two.map(order => [order.join(' ')]))('two reads and one mutation: %s', async label => {
    for (const outcome of outcomes(2)) await run(label.split(' ') as Step[], outcome)
  }, 20_000)

  // The sequences the third and fourth review rounds reported, by name.
  it('round 4: a routine read answers with pre-link facts before the link commits', async () => {
    await run(['M.start', 'R1.start', 'R1.end', 'M.end'], { r1: true, r2: true, mutation: true, returns: true, followUp: 'pending' })
  })

  it('round 3: a failed reread overlapping a failed dismissal, with the follow-up read pending', async () => {
    await run(['R1.start', 'M.start', 'R1.end', 'M.end'], { r1: false, r2: true, mutation: false, returns: false, followUp: 'pending' })
  })

  it('keeps a mutation\'s answer when an older read answers in the same tick, before the follow-up read aborts it', async () => {
    const read = deferred<PlayExecution>()
    const change = deferred<PlayExecution>()
    const playExecution = vi.fn().mockResolvedValueOnce(at(0)).mockImplementationOnce(() => read.promise)
      .mockImplementation(() => new Promise(() => {}))
    const api = { playExecution, linkOrder: vi.fn(() => change.promise) } as unknown as WorkspaceApi
    const view = renderHook(() => usePlayExecution(api, base.playId, 'open'))
    await act(async () => {})
    act(() => view.result.current.reload())
    let linking!: Promise<void>
    act(() => { linking = view.result.current.link({ orderId: 'order-1', role: 'entry' }) })
    // The link's answer and the older read's answer settle together: the read must not replace the link's facts.
    await act(async () => { change.resolve(at(2)); read.resolve(at(1)); await linking })
    expect(view.result.current.execution?.notice).toBe('v2')
    expect(view.result.current.stale).toBe(false)
  })
})

