import { useState } from 'react'
import type { Portfolio, WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'

const newPortfolio = 'new-portfolio'

export function usePortfolioDestination(portfolios: Portfolio[], api: WorkspaceApi, onCreated: () => void) {
  const [id, setId] = useState('')
  const [name, setName] = useState('')
  // Retain a confirmed creation while the shell reload is still in flight (or failed).
  const [created, setCreated] = useState<Portfolio | null>(null)
  if (created && portfolios.some(p => p.id === created.id)) setCreated(null)
  const options = created && !portfolios.some(p => p.id === created.id) ? [...portfolios, created] : portfolios
  async function resolve() {
    if (id === newPortfolio) {
      if (!name.trim()) throw new ApiError('http', 'Name the new portfolio.')
      const portfolio = await api.createPortfolio(name.trim())
      setCreated(portfolio); setId(portfolio.id); setName(''); onCreated()
      return { portfolioId: portfolio.id, created: true }
    }
    if (id && !options.some(p => p.id === id)) throw new ApiError('http', 'The selected portfolio is no longer available. Choose another portfolio.')
    if (!id && api.tradingMode === 'paper') throw new ApiError('http', 'Choose or create a paper portfolio.')
    return { portfolioId: id || null, created: false }
  }
  return { id, setId, name, setName, options, resolve }
}
