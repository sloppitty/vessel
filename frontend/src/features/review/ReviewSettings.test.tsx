import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { preferenceKey } from '@/lib/preference-scope'
import { ReviewSettings } from './ReviewSettings'
import { readReviewPreferences } from './reviewPreferences'

afterEach(() => localStorage.clear())
describe('Review settings', () => {
  it('stores a whole-number threshold in this browser and refuses others', async () => {
    const user = userEvent.setup()
    render(<ReviewSettings />)
    const input = screen.getByRole('spinbutton', { name: /fewer Plays than/ })
    expect(input).toHaveValue(5)
    await user.clear(input)
    await user.type(input, '12')
    expect(readReviewPreferences().minPlays).toBe(12)
    expect(JSON.parse(localStorage.getItem(preferenceKey('review.preferences.v1'))!)).toEqual({ minPlays: 12 })
    expect(screen.getByText(/fewer than 12 scored Plays/)).toBeVisible()
    await user.clear(input)
    await user.type(input, '-1')
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(readReviewPreferences().minPlays).toBe(12)
    await user.clear(input)
    await user.type(input, '0')
    expect(screen.getByText(/never adds imported positions by itself/)).toBeVisible()
  })
})
