import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from './workspace'
import { ApiError } from './errors'
import { monitorFrame } from './monitor'
import { isInAppNotification, isNotificationClaim, isNotificationHead, isNotificationPage, isNotificationPreferences,
  notificationPreferencesConflict, notificationReadAllConflict, type NotificationQuery } from './notifications'
import { eventCursor, fixtureEpoch } from '@/test/monitor-fixture'
import { credentialsFixture, sessionFixture } from '@/test/identity-fixture'
import { notificationCursor, notificationFixture, notificationHeadFixture, notificationPreferencesFixture, notificationWith } from '@/test/notification-fixture'

afterEach(() => vi.unstubAllGlobals())
const json = (value: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json', ...headers } })
const query: NotificationQuery = { tab: 'unread', page: 1, pageSize: 25 }

describe('notification contract', () => {
  it('accepts exact shapes with explicit nulls and rejects missing, extra or unbounded fields', () => {
    expect(isInAppNotification(notificationFixture)).toBe(true)
    const omitted: Record<string, unknown> = { ...notificationFixture }
    delete omitted.readAt
    expect(isInAppNotification(omitted)).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, extra: null })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, content: { ...notificationFixture.content, title: 'x'.repeat(161) } })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, content: { ...notificationFixture.content, title: '' } })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, content: { ...notificationFixture.content, summary: 'x'.repeat(201) } })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, content: { ...notificationFixture.content, lines: Array(9).fill('line') } })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, content: { ...notificationFixture.content, links: Array(4).fill(notificationFixture.content.links[0]) } })).toBe(false)
    const subject: Record<string, unknown> = { ...notificationFixture.content.subject }
    delete subject.instrument
    expect(isInAppNotification({ ...notificationFixture, content: { ...notificationFixture.content, subject } })).toBe(false)
  })
  it('checks enums, link targets, times and the read state', () => {
    const link = notificationFixture.content.links[0]!
    const withLink = (value: unknown) => ({ ...notificationFixture, content: { ...notificationFixture.content, links: [value] } })
    expect(isInAppNotification(withLink({ ...link, kind: 'url' }))).toBe(false)
    expect(isInAppNotification(withLink({ ...link, label: 'x'.repeat(61) }))).toBe(false)
    expect(isInAppNotification(withLink({ ...link, playId: null }))).toBe(false)
    expect(isInAppNotification(withLink({ kind: 'account-credential', label: 'Manage token', playId: null, accountId: notificationFixture.content.subject.accountId }))).toBe(true)
    expect(isInAppNotification(withLink({ ...link, href: 'https://example.test' }))).toBe(false)
    for (const field of ['kind', 'category', 'severity', 'state']) expect(isInAppNotification({ ...notificationFixture, [field]: 'other' })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, category: 'test', kind: 'test' })).toBe(true)
    expect(isInAppNotification({ ...notificationFixture, createdAt: '2026-10-17T10:00:00+02:00' })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, state: 'read' })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, state: 'read', readAt: '2026-10-17T11:00:00Z' })).toBe(true)
    expect(isInAppNotification({ ...notificationFixture, readAt: '2026-10-17T11:00:00Z' })).toBe(false)
    expect(isInAppNotification({ ...notificationFixture, state: 'withdrawn' })).toBe(true)
    expect(isInAppNotification({ ...notificationFixture, id: '00000000-0000-0000-0000-000000000000' })).toBe(false)
  })
  it('validates the head, pages, claims and preferences', () => {
    expect(isNotificationHead(notificationHeadFixture)).toBe(true)
    expect(isNotificationHead({ ...notificationHeadFixture, nextCursor: eventCursor('10') })).toBe(false)
    expect(isNotificationHead({ ...notificationHeadFixture, unreadCount: -1 })).toBe(false)
    const page = { items: [notificationFixture], page: 1, pageSize: 25, total: 1, unreadTotal: 1, allTotal: 4 }
    expect(isNotificationPage(page, query)).toBe(true)
    expect(isNotificationPage({ ...page, pageSize: 50 }, query)).toBe(false)
    expect(isNotificationPage({ ...page, items: [{ ...notificationFixture, state: 'read', readAt: '2026-10-17T11:00:00Z' }] }, query)).toBe(false)
    expect(isNotificationPage({ ...page, items: [notificationFixture, notificationFixture] }, query)).toBe(false)
    expect(isNotificationPage(page, { ...query, severity: 'critical' })).toBe(false)
    // The server clamps a page past the end to the last page, never beyond the request.
    expect(isNotificationPage({ ...page, page: 2 }, query)).toBe(false)
    expect(isNotificationClaim({ popups: [notificationWith(1), notificationWith(2), notificationWith(3)], more: 7 })).toBe(true)
    expect(isNotificationClaim({ popups: [1, 2, 3, 4].map(n => notificationWith(n)), more: 0 })).toBe(false)
    expect(isNotificationPreferences(notificationPreferencesFixture)).toBe(true)
    expect(isNotificationPreferences({ ...notificationPreferencesFixture, categories: ['test'] })).toBe(false)
    expect(isNotificationPreferences({ ...notificationPreferencesFixture, categories: ['links', 'links'] })).toBe(false)
    expect(isNotificationPreferences({ ...notificationPreferencesFixture, minimumSeverity: 'error' })).toBe(false)
    expect(isNotificationPreferences({ ...notificationPreferencesFixture, subscriptionStatus: null, failedDeliveries: 2 })).toBe(true)
    expect(isNotificationPreferences({ ...notificationPreferencesFixture, subscriptionStatus: 'paused' })).toBe(false)
    expect(isNotificationPreferences({ ...notificationPreferencesFixture, failedDeliveries: -1 })).toBe(false)
    const missing: Record<string, unknown> = { ...notificationPreferencesFixture }
    delete missing.failedDeliveries
    expect(isNotificationPreferences(missing)).toBe(false)
  })
})

describe('notification stream frames', () => {
  it('reads a notification cursor from the frame data and never from the event id', () => {
    const data = { cursor: notificationCursor(12), unreadCount: 2, enabled: true, notification: notificationFixture }
    expect(monitorFrame('notification', JSON.stringify(data), eventCursor('99'))).toEqual({ type: 'notification', frame: data })
    expect(monitorFrame('notification', JSON.stringify(data), '')).toEqual({ type: 'notification', frame: data })
    // An event cursor in the data is not a notification cursor.
    expect(monitorFrame('notification', JSON.stringify({ ...data, cursor: eventCursor('12') }), notificationCursor(12))).toBeNull()
    expect(monitorFrame('notification', JSON.stringify({ ...data, extra: 1 }))).toBeNull()
  })
  it('parses ready and reset statuses of the notification feed', () => {
    const ready = { state: 'ready', nextCursor: notificationCursor(4), unreadCount: 0, enabled: false }
    expect(monitorFrame('notifications', JSON.stringify(ready))).toEqual({ type: 'notifications', status: ready })
    expect(monitorFrame('notifications', JSON.stringify({ ...ready, state: 'reset', nextCursor: notificationCursor(1, 2) })))
      .toEqual({ type: 'notifications', status: { ...ready, state: 'reset', nextCursor: notificationCursor(1, 2) } })
    expect(monitorFrame('notifications', JSON.stringify({ ...ready, state: 'other' }))).toBeNull()
    expect(monitorFrame('notifications', JSON.stringify({ ...ready, unreadCount: '0' }))).toBeNull()
    // Event frames are unchanged.
    expect(monitorFrame('status', JSON.stringify(ready))).toBeNull()
  })
})

describe('notification client', () => {
  const client = () => createWorkspaceApi(credentialsFixture)
  it('uses this transport for every notification endpoint', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(json(notificationHeadFixture))
      .mockResolvedValueOnce(json({ items: [], page: 1, pageSize: 25, total: 0, unreadTotal: 0, allTotal: 0 }))
      .mockResolvedValueOnce(json({ popups: [], more: 0 }))
      .mockResolvedValueOnce(json({ ...notificationFixture, state: 'read', readAt: '2026-10-17T11:00:00Z' }))
      .mockResolvedValueOnce(json({ read: 1 }))
      .mockResolvedValueOnce(json(notificationPreferencesFixture))
      .mockResolvedValueOnce(json(notificationPreferencesFixture))
      .mockResolvedValueOnce(new Response(null, { status: 202 }))
    vi.stubGlobal('fetch', fetcher)
    const api = client()
    await api.notificationHead()
    await api.notifications(query)
    await api.claimNotifications()
    await api.readNotification(notificationFixture.id)
    await api.readAllNotifications(notificationHeadFixture.nextCursor)
    await api.notificationPreferences()
    await api.saveNotificationPreferences({ ...notificationPreferencesFixture, expectedRevision: 3 })
    await api.requestTestNotification()
    expect(fetcher).toHaveBeenCalledTimes(8)
    for (const [, init] of fetcher.mock.calls) {
      expect(init.credentials).toBe('same-origin')
      expect(init.headers).toMatchObject({ 'X-Vessel-User': sessionFixture.user.id })
      expect(init.headers).not.toHaveProperty('Authorization')
      expect(init.referrerPolicy).toBe('same-origin')
    }
  })

  it('sends both cursors on the stream only when a notification cursor is known', async () => {
    const fetcher = vi.fn().mockImplementation(() => Promise.resolve(new Response('', { headers: { 'Content-Type': 'text/event-stream' } })))
    vi.stubGlobal('fetch', fetcher)
    const api = client()
    const signal = new AbortController().signal
    await api.monitorStream(eventCursor('5'), signal, () => undefined)
    await api.monitorStream(eventCursor('5'), signal, () => undefined, notificationCursor(7, 1))
    const first = new URL(fetcher.mock.calls[0]![0] as string, 'http://vessel.test'), second = new URL(fetcher.mock.calls[1]![0] as string, 'http://vessel.test')
    expect(first.searchParams.has('notificationsAfter')).toBe(false)
    expect(second.searchParams.get('notificationsAfter')).toBe(`${fixtureEpoch}.1.7`)
    await expect(api.monitorStream(eventCursor('5'), signal, () => undefined, '1.2')).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('builds page queries and rejects invalid filters before a request', async () => {
    const fetcher = vi.fn().mockImplementation(() => Promise.resolve(json({ items: [], page: 1, pageSize: 50, total: 0, unreadTotal: 0, allTotal: 0 })))
    vi.stubGlobal('fetch', fetcher)
    const api = client()
    await api.notifications({ tab: 'all', category: 'links', severity: 'warning', page: 3, pageSize: 50 })
    expect(fetcher.mock.calls[0]![0]).toBe('/api/notifications?tab=all&category=links&severity=warning&page=3&pageSize=50')
    await expect(api.notifications({ tab: 'read' as 'all', page: 1, pageSize: 25 })).rejects.toBeInstanceOf(ApiError)
    await expect(api.notifications({ tab: 'all', page: 0, pageSize: 25 })).rejects.toBeInstanceOf(ApiError)
    await expect(api.notifications({ tab: 'all', page: 1, pageSize: 101 })).rejects.toBeInstanceOf(ApiError)
    expect(fetcher).toHaveBeenCalledOnce()
  })
  it('claims, reads one and reads all up to a cursor, surfacing a 409 distinctly', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(json({ popups: [notificationFixture], more: 2 }))
      .mockResolvedValueOnce(json({ ...notificationFixture, state: 'read', readAt: '2026-10-17T11:00:00Z' }))
      .mockResolvedValueOnce(json({ read: 4 }))
      .mockResolvedValueOnce(json({ title: 'Conflict', detail: 'Cursor ahead of the head.' }, 409))
    vi.stubGlobal('fetch', fetcher)
    const api = client()
    expect(await api.claimNotifications()).toEqual({ popups: [notificationFixture], more: 2 })
    expect((await api.readNotification(notificationFixture.id)).state).toBe('read')
    expect(await api.readAllNotifications(notificationCursor(9))).toEqual({ read: 4 })
    await expect(api.readAllNotifications(notificationCursor(9))).rejects.toMatchObject({ status: 409, message: notificationReadAllConflict })
    expect(fetcher.mock.calls.map(call => [call[0], call[1].method])).toEqual([
      ['/api/notifications/claim', 'POST'], [`/api/notifications/${notificationFixture.id}/read`, 'POST'],
      ['/api/notifications/read', 'POST'], ['/api/notifications/read', 'POST']])
    expect(JSON.parse(fetcher.mock.calls[2]![1].body as string)).toEqual({ upTo: notificationCursor(9) })
    await expect(api.readAllNotifications('9')).rejects.toMatchObject({ kind: 'invalid-response' })
    await expect(api.readNotification('not-a-guid')).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetcher).toHaveBeenCalledTimes(4)
  })
  it('rejects a read answer for another notification', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(notificationWith(2))))
    await expect(client().readNotification(notificationFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('saves preferences with the expected revision and reports a stale revision as a conflict', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(json({ ...notificationPreferencesFixture, revision: 4 }))
      .mockResolvedValueOnce(json({ title: 'Conflict' }, 409))
    vi.stubGlobal('fetch', fetcher)
    const api = client()
    const fields = { enabled: true, minimumSeverity: 'warning' as const, categories: ['links' as const], expectedRevision: 3 }
    expect((await api.saveNotificationPreferences(fields)).revision).toBe(4)
    expect(fetcher.mock.calls[0]![1].method).toBe('PUT')
    expect(JSON.parse(fetcher.mock.calls[0]![1].body as string)).toEqual(fields)
    await expect(api.saveNotificationPreferences(fields)).rejects.toMatchObject({ status: 409, message: notificationPreferencesConflict })
    await expect(api.saveNotificationPreferences({ ...fields, categories: ['test' as 'links'] })).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('accepts a test request and returns the cooldown from Retry-After', async () => {
    vi.useFakeTimers({ now: Date.parse('2026-10-17T10:00:00Z') })
    const fetcher = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 202 }))
      .mockResolvedValueOnce(json({ title: 'A test notification was requested moments ago.' }, 429, { 'Retry-After': '7' }))
    vi.stubGlobal('fetch', fetcher)
    const api = client()
    await expect(api.requestTestNotification()).resolves.toBeUndefined()
    await expect(api.requestTestNotification()).rejects.toMatchObject({ status: 429, retryAtUtc: '2026-10-17T10:00:07.000Z' })
    vi.useRealTimers()
  })
})
