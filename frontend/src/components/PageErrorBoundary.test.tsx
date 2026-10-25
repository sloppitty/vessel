import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { PageErrorBoundary, PageLoadFallback } from './PageErrorBoundary'

function Child({ fail }: { fail: boolean }) {
  if (fail) throw new Error('session private-token database-password')
  return <p>Recovered content</p>
}

describe('page error boundary', () => {
  it('uses fixed safe text, offers retry, and resets on navigation without remounting the parent', async () => {
    let fail = true
    const view = render(<PageErrorBoundary resetKey="review"><Child fail={fail} /></PageErrorBoundary>, { onCaughtError: () => {} })
    expect(screen.getByRole('alert')).toHaveTextContent('This page could not be displayed.')
    expect(screen.queryByText(/private-token|database-password/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reload browser' })).toBeEnabled()
    fail = false
    view.rerender(<PageErrorBoundary resetKey="review"><Child fail={fail} /></PageErrorBoundary>)
    // Changing children alone must not silently dismiss a failure.
    expect(screen.getByRole('alert')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(screen.getByText('Recovered content')).toBeInTheDocument()
    view.rerender(<PageErrorBoundary resetKey="review"><Child fail /></PageErrorBoundary>)
    expect(screen.getByRole('alert')).toBeInTheDocument()
    view.rerender(<PageErrorBoundary resetKey="overview"><Child fail={false} /></PageErrorBoundary>)
    expect(screen.getByText('Recovered content')).toBeInTheDocument()
  })

  it.each(['render', 'module'])('keeps browser reload explicit with an unsaved-work warning for %s failures', kind => {
    render(kind === 'render' ? <PageErrorBoundary><Child fail /></PageErrorBoundary> : <PageLoadFallback />, { onCaughtError: () => {} })
    expect(screen.getByRole('alert')).toHaveTextContent('Reloading the browser discards unsaved work.')
    const reload = vi.fn()
    vi.stubGlobal('window', { location: { reload } })
    fireEvent.click(screen.getByRole('button', { name: 'Reload browser' }))
    expect(reload).toHaveBeenCalledTimes(1)
  })

  it('does not log the thrown value or a component stack', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => {})
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    render(<PageErrorBoundary><Child fail /></PageErrorBoundary>, { onCaughtError: () => {} })
    expect(error).not.toHaveBeenCalled()
    expect(warn).not.toHaveBeenCalled()
  })
})
