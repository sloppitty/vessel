import { describe, expect, it } from 'vitest'
import { isPlayFunding } from './play-funding'
import { isPlayExecution } from './plays'
import recorded from '@/test/contracts/monitor-api.json?raw'

const execution = (JSON.parse(recorded) as { playExecution: Record<string, unknown> }).playExecution
const sole = { state: 'sole', reason: null, amountUsd: '-0.1234567890123456789012345678', asset: 'USDC', paymentCount: 2,
  fromUtc: '2026-10-20T12:00:00+00:00', toUtc: '2026-10-20T15:00:00+00:00' }
const none = { amountUsd: null, asset: null, paymentCount: null, fromUtc: null, toUtc: null }

describe('settled funding beside a Play (phase 2, 9b)', () => {
  it('accepts a sole sum with its window and each other state with its reason only', () => {
    expect(isPlayFunding(sole)).toBe(true)
    expect(isPlayFunding({ ...sole, asset: null, amountUsd: '0', paymentCount: 0 })).toBe(true)
    expect(isPlayFunding({ state: 'shared', reason: 'other-play', ...none })).toBe(true)
    // A fill split between plays (Phase 2, item 11) makes the position shared, never incomplete.
    expect(isPlayFunding({ state: 'shared', reason: 'allocated', ...none })).toBe(true)
    expect(isPlayFunding({ state: 'incomplete', reason: 'allocated', ...none })).toBe(false)
    expect(isPlayFunding({ state: 'incomplete', reason: 'flat-start-unproven', ...none })).toBe(true)
    expect(isPlayFunding({ state: 'incomplete', reason: 'changing', ...none })).toBe(true)
    expect(isPlayFunding({ state: 'unavailable', reason: 'no-funding-read', ...none })).toBe(true)
  })

  it.each([
    { ...sole, reason: 'other-play' },
    { ...sole, amountUsd: -0.12 },
    { ...sole, amountUsd: '1e-3' },
    { ...sole, amountUsd: null },
    { ...sole, paymentCount: -1 },
    { ...sole, fromUtc: sole.toUtc, toUtc: sole.fromUtc },
    { ...sole, fromUtc: null },
    { state: 'shared', reason: 'history-gap', ...none },
    { state: 'incomplete', reason: 'other-play', ...none },
    { state: 'incomplete', reason: null, ...none },
    { state: 'shared', reason: 'other-play', ...none, amountUsd: '1' },
    { state: 'incomplete', reason: 'history-gap', ...none, paymentCount: 0 },
    { state: 'allocated', reason: null, ...none },
  ])('rejects an inconsistent funding line %#', value => {
    expect(isPlayFunding(value)).toBe(false)
  })

  it('accepts an execution with or without the line, never a malformed one', () => {
    expect('funding' in execution).toBe(false)
    expect(isPlayExecution(execution)).toBe(true)
    expect(isPlayExecution({ ...execution, funding: sole })).toBe(true)
    expect(isPlayExecution({ ...execution, funding: null })).toBe(false)
    expect(isPlayExecution({ ...execution, funding: { ...sole, state: 'shared' } })).toBe(false)
  })
})
