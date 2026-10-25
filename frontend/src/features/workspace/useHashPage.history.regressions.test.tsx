import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { useHashPage } from './useHashPage'

afterEach(() => window.history.replaceState(null, '', '/'))

it('accepts browser back/forward with the latest inline callback and never replays an accepted hash', async () => {
  window.history.replaceState(null, '', '/#settings')
  const navigate = vi.fn(), external = vi.fn()
  const { result, rerender } = renderHook(({ tab }) =>
    useHashPage(navigate, page => external(page, tab)), { initialProps: { tab: 'appearance' } })
  expect(result.current.page).toBe('settings')
  act(() => result.current.navigate('accounts'))
  rerender({ tab: 'profile' })
  act(() => window.history.back())
  await waitFor(() => expect(result.current.page).toBe('settings'))
  expect(external).toHaveBeenLastCalledWith('settings', 'profile')
  act(() => window.history.forward())
  await waitFor(() => expect(result.current.page).toBe('accounts'))
  expect(external).toHaveBeenCalledTimes(2)
  act(() => window.dispatchEvent(new HashChangeEvent('hashchange')))
  expect(external).toHaveBeenCalledTimes(2)
  expect(navigate).toHaveBeenCalledTimes(3)
})
