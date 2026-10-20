import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { EvidenceImageLoader, evidenceImageLimit } from './evidence-images'

interface Pending { id: string; signal: AbortSignal; resolve: (blob: Blob) => void; reject: (cause: unknown) => void }

/** A read whose responses the test settles by hand, recording each request's signal. */
function reads() {
  const pending: Pending[] = []
  const read = vi.fn((id: string, signal: AbortSignal) => new Promise<Blob>((resolve, reject) => { pending.push({ id, signal, resolve, reject }) }))
  const take = (id: string) => {
    const index = pending.findIndex(item => item.id === id)
    if (index < 0) throw new Error(`No pending read for ${id}`)
    return pending.splice(index, 1)[0]!
  }
  return { pending, read, take }
}
const png = () => new Blob(['png'], { type: 'image/png' })
const settle = () => new Promise(resolve => setTimeout(resolve, 0))

describe('saved evidence originals', () => {
  it('loads at most three originals at a time and starts the next as one finishes', async () => {
    const { pending, read, take } = reads()
    const loader = new EvidenceImageLoader(read)
    loader.want(['a', 'b', 'c', 'd', 'e'])
    expect(evidenceImageLimit).toBe(3)
    expect(pending.map(item => item.id)).toEqual(['a', 'b', 'c'])
    expect(loader.state('d')).toEqual({ status: 'loading' })
    const image = png()
    take('b').resolve(image)
    await settle()
    expect(loader.state('b')).toEqual({ status: 'loaded', image })
    expect(pending.map(item => item.id)).toEqual(['a', 'c', 'd'])
  })

  it('tells a missing original (404) from an unavailable one, and retries only on request', async () => {
    const { pending, read, take } = reads()
    const loader = new EvidenceImageLoader(read)
    const changed = vi.fn()
    loader.subscribe(changed)
    loader.want(['gone', 'down', 'offline'])
    take('gone').reject(new ApiError('http', 'This play or image is no longer available.', 404))
    take('down').reject(new ApiError('http', 'The service is unavailable.', 503))
    take('offline').reject(new ApiError('unavailable', 'The request did not complete.'))
    await settle()
    expect(loader.state('gone')).toEqual({ status: 'missing' })
    expect(loader.state('down')).toEqual({ status: 'unavailable', message: 'The service is unavailable.' })
    expect(loader.state('offline')).toMatchObject({ status: 'unavailable' })
    expect(changed).toHaveBeenCalled()
    // Nothing retries by itself.
    expect(pending).toHaveLength(0)
    expect(read).toHaveBeenCalledTimes(3)

    loader.retry('down')
    expect(loader.state('down')).toEqual({ status: 'loading' })
    const image = png()
    take('down').resolve(image)
    await settle()
    expect(loader.state('down')).toEqual({ status: 'loaded', image })
    // A loaded or unknown original is not read again.
    loader.retry('down')
    loader.retry('unknown')
    expect(read).toHaveBeenCalledTimes(4)
  })

  it('reports an identifier the API refuses before sending as unavailable, not as a thrown error', async () => {
    const loader = new EvidenceImageLoader(() => { throw new ApiError('invalid-response', 'The image identifier is invalid.') })
    loader.want(['not-a-guid'])
    await settle()
    expect(loader.state('not-a-guid')).toEqual({ status: 'unavailable', message: 'The image identifier is invalid.' })
  })

  it('aborts and forgets originals that leave the open Play, and ignores their late answers', async () => {
    const { pending, read, take } = reads()
    const loader = new EvidenceImageLoader(read)
    loader.want(['a', 'b', 'c', 'd'])
    take('a').resolve(png())
    await settle()
    expect(loader.state('a').status).toBe('loaded')
    const [b, c, d] = ['b', 'c', 'd'].map(take) as [Pending, Pending, Pending]
    // Another Play opens: its originals load instead, without waiting for the abandoned reads to settle.
    loader.want(['x'])
    for (const abandoned of [b, c, d]) expect(abandoned.signal.aborted).toBe(true)
    expect(pending.map(item => item.id)).toEqual(['x'])
    b.resolve(png())
    c.reject(new ApiError('http', 'The service is unavailable.', 503))
    d.reject(new ApiError('unavailable', 'The request did not complete.'))
    await settle()
    expect([...loader.snapshot.keys()]).toEqual(['x'])
    expect(loader.state('x')).toEqual({ status: 'loading' })
    expect(read).toHaveBeenCalledTimes(5)
  })

  it('keeps loaded originals when the list only grows, and restarts after a reset', async () => {
    const { pending, read, take } = reads()
    const loader = new EvidenceImageLoader(read)
    loader.want(['a'])
    const image = png()
    take('a').resolve(image)
    await settle()
    loader.want(['a', 'b'])
    expect(loader.state('a')).toEqual({ status: 'loaded', image })
    expect(pending.map(item => item.id)).toEqual(['b'])
    const b = take('b')
    loader.reset()
    expect(b.signal.aborted).toBe(true)
    expect(loader.snapshot.size).toBe(0)
    loader.want(['a', 'b'])
    expect(pending.map(item => item.id)).toEqual(['a', 'b'])
    expect(read).toHaveBeenCalledTimes(4)
  })
})
