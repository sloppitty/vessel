import { expect, it } from 'vitest'
import type { PlanRevision, PlayPlan } from '@/api/plays'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { isPlanText } from './numbers'
import { stopHistory, stopText } from './stop-history'

it.each([
  ['0.000000000001', '0.0000000000004', '0.0000000000005'],
  ['100', '95.0000000000001', '95.0000000000002'],
  ['9007199254740994', '9007199254740993', '9007199254740994'],
])('preserves distinct saved stop prices for entry %s', (entryPrice, original, final) => {
  const plan = (value: string): PlayPlan => ({
    ...savedPlayFixture.plan,
    entries: [{
      ...savedPlayFixture.plan.entries[0]!,
      price: entryPrice,
      stops: [{ id: 's1', unit: 'price', value, share: '100' }],
    }],
  })
  const revision = (number: number, value: string): PlanRevision => ({
    number, plan: plan(value), status: 'open', reason: 'Move stop',
    createdAtUtc: '2026-11-04T00:00:00Z',
  })
  for (const value of [entryPrice, original, final]) expect(isPlanText(value)).toBe(true)
  const [entry] = stopHistory([revision(1, original), revision(2, final)], plan(final))
  expect(entry!.moves).toBe(1)
  expect([stopText(entry!.original[0]!), stopText(entry!.final[0]!)]).toEqual([original, final])
})
