import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { isPlanRuleSnapshot, isPlayRuleState, type PlayHistory, type PlayRule } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { IfThenRuleLine, RuleDialog } from './IfThenRule'
import { HistoryDialog, PreflightDialog } from './PlayDialogs'

const at = '2026-10-22T10:00:00Z'
const source = '00000000-0000-4000-8000-000000000002'
const rule = (version: number, then = 'wait for the retest'): PlayRule =>
  ({ version, if: 'price breaks out on low volume', then, sourcePlayId: source, sourceTitle: 'BTC breakout', createdAtUtc: at })

describe('the if-then rule (Phase 2, item 3)', () => {
  it('reads the rule state and the snapshot strictly', () => {
    expect(isPlayRuleState({ active: rule(1), tradingMode: 'real' })).toBe(true)
    expect(isPlayRuleState({ active: null, tradingMode: 'paper' })).toBe(true)
    expect(isPlayRuleState({ active: { ...rule(1), then: ' ' }, tradingMode: 'real' })).toBe(false)
    expect(isPlayRuleState({ active: { ...rule(0) }, tradingMode: 'real' })).toBe(false)
    const snapshot = { version: 2, if: 'a', then: 'b', sourcePlayId: null }
    expect(isPlanRuleSnapshot({ ...snapshot, answer: null, reason: null })).toBe(true)
    expect(isPlanRuleSnapshot({ ...snapshot, answer: 'exception', reason: 'News day' })).toBe(true)
    // An exception carries its reason, and only an exception does.
    expect(isPlanRuleSnapshot({ ...snapshot, answer: 'exception', reason: null })).toBe(false)
    expect(isPlanRuleSnapshot({ ...snapshot, answer: 'acknowledged', reason: 'Why' })).toBe(false)
  })

  it('shows the active rule in the Thesis tab, or a way to write one, and nothing before it is read', async () => {
    const user = userEvent.setup()
    const controls = { state: null, onWrite: vi.fn(), onPromote: vi.fn(), onArchive: vi.fn() }
    const view = render(<IfThenRuleLine controls={controls} />)
    expect(screen.queryByRole('group', { name: 'If-then rule' })).toBeNull()
    view.rerender(<IfThenRuleLine controls={{ ...controls, state: { active: null, tradingMode: 'real' } }} />)
    expect(screen.getByRole('group', { name: 'If-then rule' })).toHaveTextContent('No if-then rule yet.')
    await user.click(screen.getByRole('button', { name: 'Write a rule' }))
    expect(controls.onWrite).toHaveBeenCalledOnce()
    view.rerender(<IfThenRuleLine controls={{ ...controls, state: { active: rule(3), tradingMode: 'real' } }} />)
    expect(screen.getByRole('group', { name: 'If-then rule' }))
      .toHaveTextContent('Your if-then rule · version 3If price breaks out on low volume, then wait for the retest · from BTC breakout')
    await user.click(screen.getByRole('button', { name: 'Archive the rule' }))
    expect(controls.onArchive).toHaveBeenCalledWith(rule(3))
    expect(screen.getByRole('button', { name: 'Change the rule' })).toBeEnabled()
    // The source Play opens from the rule (Sol round 1).
    const onOpenSource = vi.fn()
    view.rerender(<IfThenRuleLine controls={{ ...controls, onOpenSource, state: { active: rule(3), tradingMode: 'real' } }} />)
    await user.click(screen.getByRole('button', { name: 'BTC breakout' }))
    expect(onOpenSource).toHaveBeenCalledWith(source)
  })

  it('writes a rule with both parts, trimmed, and names a promoted next action', async () => {
    const user = userEvent.setup()
    const onSave = vi.fn()
    render(<RuleDialog active={null} draft={{ if: '', then: 'Wait for the retest', source: { playId: source, title: 'BTC breakout' }, expectedVersion: null }}
      pending={false} error={null} onSave={onSave} onClose={vi.fn()} />)
    expect(screen.getByText('From the next action of BTC breakout.')).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Then' })).toHaveValue('Wait for the retest')
    const save = screen.getByRole('button', { name: 'Save the rule' })
    expect(save).toBeDisabled()
    await user.type(screen.getByRole('textbox', { name: 'If' }), '  price breaks out  ')
    await user.click(save)
    expect(onSave).toHaveBeenCalledWith({ if: 'price breaks out', then: 'Wait for the retest' })
  })

  it('asks for the rule at Plan it: acknowledged, an exception with a reason, or no answer', async () => {
    const user = userEvent.setup()
    const onPlan = vi.fn()
    const checks = [{ code: 'thesis' as const, outcome: 'passed' as const }]
    const view = render(<PreflightDialog checks={checks} pending={false} error={null} onPlan={onPlan} onClose={vi.fn()} rule={rule(2)} />)
    expect(screen.getByRole('group', { name: 'Your if-then rule' })).toHaveTextContent('If price breaks out on low volume, then wait for the retest')
    const plan = screen.getByRole('button', { name: 'Plan it anyway' })
    // No answer never blocks planning; the shown version is still sent, so only a shown rule is recorded.
    await user.click(plan)
    expect(onPlan).toHaveBeenLastCalledWith(null, { version: 2 })
    await user.click(screen.getByRole('radio', { name: 'An exception this time' }))
    expect(plan).toBeDisabled()
    await user.type(screen.getByRole('textbox', { name: 'Why this is an exception' }), '  Earnings gap, sized down.  ')
    await user.click(plan)
    expect(onPlan).toHaveBeenLastCalledWith(null, { version: 2, answer: 'exception', reason: 'Earnings gap, sized down.' })
    await user.click(screen.getByRole('radio', { name: 'Acknowledged' }))
    await user.click(plan)
    expect(onPlan).toHaveBeenLastCalledWith(null, { version: 2, answer: 'acknowledged' })
    // A rule changed while the checklist is open is asked again rather than answered for.
    view.rerender(<PreflightDialog checks={checks} pending={false} error={null} onPlan={onPlan} onClose={vi.fn()} rule={rule(3, 'stand aside')} />)
    expect(screen.getByRole('radio', { name: 'Acknowledged' })).not.toBeChecked()
    await user.click(plan)
    expect(onPlan).toHaveBeenLastCalledWith(null, { version: 3 })
    // Without a rule shown (none active, or a failed read) nothing is sent.
    view.rerender(<PreflightDialog checks={checks} pending={false} error={null} onPlan={onPlan} onClose={vi.fn()} />)
    expect(screen.queryByRole('group', { name: 'Your if-then rule' })).toBeNull()
    await user.click(plan)
    expect(onPlan).toHaveBeenLastCalledWith(null, null)
  })

  it('lists the rule seen at Plan it and the answer in the plan history', async () => {
    const plan = { direction: 'long', sizingMode: 'margin', size: '100', leverage: '1', budgetOverride: null, entries: [],
      notes: { thesis: '', invalidation: '', management: '', review: '' } }
    const history = { statusChanges: [], reviews: [{ number: 1, action: 'edited', occurredAtUtc: at, review: '',
      structuredReview: { adherence: null, mistakeTag: null, lesson: '', nextAction: '', rule: 'deviated' } }], revisions: [
      { number: 1, status: 'planned', reason: '', createdAtUtc: at, plan, preflight: [],
        rule: { version: 2, if: 'price breaks out', then: 'wait', sourcePlayId: null, answer: 'exception', reason: 'Earnings gap' } }] } as unknown as PlayHistory
    render(<HistoryDialog api={{ playHistory: vi.fn().mockResolvedValue(history) } as unknown as WorkspaceApi} playId="one" onClose={vi.fn()} />)
    expect(await screen.findByText('If-then rule, version 2: if price breaks out, then wait · exception: Earnings gap.')).toBeInTheDocument()
    expect(screen.getByText('Followed the rule: deviated')).toBeInTheDocument()
  })
})
