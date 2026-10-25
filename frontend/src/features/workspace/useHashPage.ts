import { useCallback, useEffect, useRef, useState } from 'react'

const pageIds = ['overview', 'plays', 'review', 'portfolios', 'accounts', 'activity', 'notifications', 'settings'] as const
export type Page = typeof pageIds[number]
const pageFromHash = (): Page => pageIds.find(id => id === window.location.hash.slice(1)) ?? 'overview'

export function useHashPage(onNavigate: (page: Page) => void, onHashChange?: (page: Page) => void) {
  const [page, setPage] = useState(pageFromHash)
  const acceptedHash = useRef(window.location.hash)
  const accept = useCallback((next: Page) => {
    setPage(next)
    onNavigate(next)
    window.scrollTo({ top: 0, left: 0, behavior: 'auto' })
  }, [onNavigate])
  useEffect(() => {
    const handle = () => {
      if (acceptedHash.current === window.location.hash) return
      acceptedHash.current = window.location.hash
      const next = pageFromHash()
      accept(next)
      onHashChange?.(next)
    }
    window.addEventListener('hashchange', handle)
    return () => window.removeEventListener('hashchange', handle)
  }, [accept, onHashChange])
  const navigate = (next: Page) => {
    // Explicit shell navigation already accepts the page. Do not replay it as an
    // external hashchange (which would erase a just-opened account or breadcrumb).
    if (window.location.hash !== `#${next}`) window.history.pushState(null, '', `#${next}`)
    acceptedHash.current = window.location.hash
    accept(next)
  }
  return { page, navigate }
}
