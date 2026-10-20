import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { badgeText, notificationsLabel, useCountAnnouncement, workerStatus } from './unread'

beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())

describe('unread count', () => {
  it('caps the badge at 99+ and names the link with its visible text first', () => {
    expect(badgeText(7)).toBe('7')
    expect(badgeText(99)).toBe('99')
    expect(badgeText(100)).toBe('99+')
    expect(notificationsLabel(0)).toBe('Notifications')
    expect(notificationsLabel(3)).toBe('Notifications, 3 unread')
    expect(notificationsLabel(250)).toBe('Notifications, 99+ unread')
  })
  it('announces a changed count once it settles, never the first count or every frame', () => {
    const view = renderHook(({ count, known }) => useCountAnnouncement(count, known), { initialProps: { count: 0, known: false } })
    view.rerender({ count: 4, known: true })
    act(() => { vi.advanceTimersByTime(5000) })
    expect(view.result.current).toBe('')
    view.rerender({ count: 5, known: true })
    act(() => { vi.advanceTimersByTime(500) })
    view.rerender({ count: 6, known: true })
    act(() => { vi.advanceTimersByTime(500) })
    view.rerender({ count: 7, known: true })
    act(() => { vi.advanceTimersByTime(1499) })
    expect(view.result.current).toBe('')
    act(() => { vi.advanceTimersByTime(1) })
    expect(view.result.current).toBe('7 unread notifications')
    view.rerender({ count: 0, known: true })
    act(() => { vi.advanceTimersByTime(1500) })
    expect(view.result.current).toBe('No unread notifications')
  })
  it('describes the worker status', () => {
    expect(workerStatus(true, null, 'utc')).toBe('Delivering')
    expect(workerStatus(false, '2026-10-17T14:02:00Z', 'utc')).toBe('Notification worker offline since 17 Oct 2026, 14:02:00 UTC')
    expect(workerStatus(false, null, 'local')).toMatch(/has not reported yet/)
  })
})
