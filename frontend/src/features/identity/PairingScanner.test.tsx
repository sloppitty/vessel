import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PairingScanner } from './PairingScanner'
import { pairingUrl } from './pairing-link'

const jsqr = vi.hoisted(() => ({ loaded: vi.fn(), decode: vi.fn() }))
vi.mock('jsqr', () => { jsqr.loaded(); return { default: jsqr.decode } })

const origin = 'https://vessel.test'
const secret = 'Zx3_-Q'.padEnd(43, 'b')
let tracks: Array<{ stop: ReturnType<typeof vi.fn> }>
let getUserMedia: ReturnType<typeof vi.fn>
let detected: string[][]
let detect: ReturnType<typeof vi.fn>

function stubDetector(supported = ['qr_code', 'ean_13']) {
  detect = vi.fn(async () => (detected.shift() ?? []).map(rawValue => ({ rawValue })))
  class Detector { static getSupportedFormats = vi.fn(async () => supported); detect = detect }
  vi.stubGlobal('BarcodeDetector', Detector)
}

beforeEach(() => {
  detected = []
  tracks = [{ stop: vi.fn() }, { stop: vi.fn() }]
  getUserMedia = vi.fn(async () => ({ getTracks: () => tracks }))
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } })
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
  jsqr.loaded.mockClear()
  jsqr.decode.mockReset()
  stubDetector()
})
afterEach(() => { Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: undefined }) })

function mount(overrides: Partial<Parameters<typeof PairingScanner>[0]> = {}) {
  const handlers = { onSecret: vi.fn(), onType: vi.fn(), onBack: vi.fn() }
  const view = render(<PairingScanner origin={origin} {...handlers} {...overrides} />)
  return { ...view, ...handlers, user: userEvent.setup() }
}
const stopped = () => tracks.every(track => track.stop.mock.calls.length > 0)

describe('PairingScanner', () => {
  it('does not touch the camera until the person taps', async () => {
    const { user } = mount()
    expect(getUserMedia).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    expect(getUserMedia).toHaveBeenCalledWith({ video: { facingMode: 'environment' }, audio: false })
  })

  it('accepts a link on this origin, stops every track first and hands over only the secret', async () => {
    detected = [[], [pairingUrl(origin, secret)]]
    const { user, onSecret } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    await waitFor(() => expect(onSecret).toHaveBeenCalledWith(secret))
    expect(stopped()).toBe(true)
    expect(onSecret).toHaveBeenCalledTimes(1)
    expect(detect).toHaveBeenCalledTimes(2)
    // No decoder library is loaded where the browser can decode QR codes itself.
    expect(jsqr.loaded).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Start camera' })).toBeVisible()
  })

  it('refuses a code for another Vessel, keeps scanning and then accepts the right one', async () => {
    detected = [[pairingUrl('https://other.test', secret)], [], [pairingUrl(origin, secret)]]
    const { user, onSecret } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    expect(await screen.findByText('This code is for a different Vessel.')).toBeVisible()
    expect(onSecret).not.toHaveBeenCalled()
    expect(tracks[0]!.stop).not.toHaveBeenCalled()
    await waitFor(() => expect(onSecret).toHaveBeenCalledWith(secret))
  })

  it('ignores other content without a message and keeps the camera on', async () => {
    detected = [['https://example.com/menu'], ['hello'], [`${origin}/#plays`]]
    const { user, onSecret } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    await waitFor(() => expect(detect).toHaveBeenCalledTimes(4))
    expect(onSecret).not.toHaveBeenCalled()
    expect(screen.queryByText('This code is for a different Vessel.')).not.toBeInTheDocument()
    expect(tracks[0]!.stop).not.toHaveBeenCalled()
  })

  it('loads the pure-JavaScript decoder only when the browser has none, and only after the tap', async () => {
    vi.unstubAllGlobals()
    vi.stubGlobal('BarcodeDetector', undefined)
    Object.defineProperty(HTMLVideoElement.prototype, 'videoWidth', { configurable: true, get: () => 1280 })
    Object.defineProperty(HTMLVideoElement.prototype, 'videoHeight', { configurable: true, get: () => 720 })
    const context = { drawImage: vi.fn(), getImageData: vi.fn(() => ({ data: new Uint8ClampedArray(4), width: 640, height: 360 })) }
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D)
    jsqr.decode.mockReturnValueOnce(null).mockReturnValue({ data: pairingUrl(origin, secret) })
    const { user, onSecret } = mount()
    expect(jsqr.loaded).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    await waitFor(() => expect(onSecret).toHaveBeenCalledWith(secret))
    expect(jsqr.loaded).toHaveBeenCalledTimes(1)
    expect(context.drawImage).toHaveBeenCalled()
    expect(stopped()).toBe(true)
  })

  it('falls back to the pure-JavaScript decoder when BarcodeDetector cannot read QR codes', async () => {
    stubDetector(['ean_13'])
    Object.defineProperty(HTMLVideoElement.prototype, 'videoWidth', { configurable: true, get: () => 640 })
    Object.defineProperty(HTMLVideoElement.prototype, 'videoHeight', { configurable: true, get: () => 480 })
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage: vi.fn(), getImageData: () => ({ data: new Uint8ClampedArray(4), width: 640, height: 480 }) } as unknown as CanvasRenderingContext2D)
    jsqr.decode.mockReturnValue({ data: pairingUrl(origin, secret) })
    const { user, onSecret } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    await waitFor(() => expect(onSecret).toHaveBeenCalledWith(secret))
    expect(detect).not.toHaveBeenCalled()
  })

  it('stops the camera on Back and on unmount', async () => {
    const first = mount()
    await first.user.click(screen.getByRole('button', { name: 'Start camera' }))
    await waitFor(() => expect(detect).toHaveBeenCalled())
    await first.user.click(screen.getByRole('button', { name: 'Back' }))
    expect(first.onBack).toHaveBeenCalled()
    first.unmount()
    expect(stopped()).toBe(true)

    tracks = [{ stop: vi.fn() }]
    const second = mount()
    await second.user.click(screen.getByRole('button', { name: 'Start camera' }))
    await waitFor(() => expect(detect).toHaveBeenCalledTimes(2))
    second.unmount()
    expect(tracks[0]!.stop).toHaveBeenCalled()
  })

  it('stops the camera when the page is hidden and waits for another tap', async () => {
    const { user } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    await waitFor(() => expect(detect).toHaveBeenCalled())
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' })
    act(() => { document.dispatchEvent(new Event('visibilitychange')) })
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' })
    expect(stopped()).toBe(true)
    expect(await screen.findByRole('button', { name: 'Start camera' })).toBeVisible()
    const calls = detect.mock.calls.length
    await new Promise(resolve => setTimeout(resolve, 600))
    expect(detect.mock.calls.length).toBe(calls)
  })

  it('stops a stream that arrives after the scanner closed', async () => {
    let resolve!: (stream: unknown) => void
    getUserMedia.mockImplementationOnce(() => new Promise(done => { resolve = done }))
    const { user, unmount } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    unmount()
    resolve({ getTracks: () => tracks })
    await waitFor(() => expect(stopped()).toBe(true))
  })

  it('explains a refused permission and offers the camera app and another try', async () => {
    getUserMedia.mockRejectedValueOnce(new DOMException('denied', 'NotAllowedError'))
    const { user } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Camera access is blocked for this site')
    expect(screen.getByRole('alert')).toHaveTextContent('camera app')
    expect(screen.getByRole('button', { name: 'Try the camera again' })).toBeVisible()
  })

  it('reports another camera failure without claiming a refusal', async () => {
    getUserMedia.mockRejectedValueOnce(new DOMException('none', 'NotFoundError'))
    const { user } = mount()
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The camera could not start')
  })

  it('offers Enter a code instead before and during scanning', async () => {
    const { user, onType } = mount()
    await user.click(screen.getByRole('button', { name: 'Enter a code instead' }))
    expect(onType).toHaveBeenCalledTimes(1)
  })
})
