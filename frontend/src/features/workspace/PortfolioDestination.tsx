import { Input } from '@/components/ui/input'
import type { usePortfolioDestination } from './usePortfolioDestination'
import { NativeSelect } from '@/components/ui/native-select'

const newPortfolio = 'new-portfolio'

export function PortfolioDestination({ destination, inputId, label = 'Portfolio', disabled, required = false }: {
  destination: ReturnType<typeof usePortfolioDestination>; inputId: string; label?: string; disabled: boolean; required?: boolean
}) {
  return <>
    <label htmlFor={inputId}>{label}</label><NativeSelect id={inputId} value={destination.id} disabled={disabled} required={required}
      onChange={event => destination.setId(event.target.value)}>
      <option value="">{required ? 'Choose a paper portfolio' : 'No portfolio'}</option>{destination.options.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      {destination.id && destination.id !== newPortfolio && !destination.options.some(p => p.id === destination.id) &&
        <option value={destination.id} disabled>Portfolio no longer available</option>}
      <option value={newPortfolio}>New portfolio…</option>
    </NativeSelect>
    {destination.id === newPortfolio && <><label htmlFor={`${inputId}-name`}>New portfolio name</label>
      <Input id={`${inputId}-name`} value={destination.name} onChange={event => destination.setName(event.target.value)} maxLength={200} disabled={disabled} /></>}
  </>
}
