import { useId, useState } from 'react'
import { BarChart3 } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { isMinPlays, maxMinPlays, useReviewPreferences } from './reviewPreferences'
import './review-settings.css'

const parse = (text: string) => text.trim() === '' ? NaN : Number(text)

/**
 * The Review section of Settings: when Review adds imported positions by itself. Saved in this browser when a valid
 * value is completed (blur or Enter), never from a keystroke prefix.
 */
export function ReviewSettings() {
  const id = useId()
  const [preferences, update] = useReviewPreferences()
  const [text, setText] = useState(String(preferences.minPlays))
  // Another tab or user scope can change the stored value; show it unless an edit is pending.
  const [shown, setShown] = useState(preferences.minPlays)
  if (shown !== preferences.minPlays) {
    setShown(preferences.minPlays)
    setText(String(preferences.minPlays))
  }
  const value = parse(text)
  const valid = isMinPlays(value)
  const commit = () => { if (valid && value !== preferences.minPlays) update({ minPlays: value }) }
  return <section className="shell-panel settings-panel review-settings" aria-labelledby={`${id}-heading`}>
    <BarChart3 size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Review</h2>
    <label className="review-settings-field" htmlFor={`${id}-min`}>Include imported positions while there are fewer Plays than
      <Input id={`${id}-min`} type="number" inputMode="numeric" min={0} max={maxMinPlays} step={1} value={text}
        aria-invalid={!valid} aria-describedby={`${id}-hint`}
        onChange={event => setText(event.target.value)} onBlur={commit}
        onKeyDown={event => { if (event.key === 'Enter') commit() }} /></label>
    <p id={`${id}-hint`} className={valid ? undefined : 'review-settings-error'}>{valid
      ? value === 0 ? 'Review never adds imported positions by itself; its toggle still can.'
        : `Review adds closed imported positions while the selection has fewer than ${value} scored ${value === 1 ? 'Play' : 'Plays'}. Its toggle overrides this.`
      : `Enter a whole number from 0 to ${maxMinPlays}.`}</p>
  </section>
}
