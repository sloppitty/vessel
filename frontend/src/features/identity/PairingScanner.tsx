import { useCallback, useEffect, useRef, useState } from 'react'
import { Camera, CameraOff, Keyboard } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { classifyScan } from './pairing-link'
import { SignInBrand } from './IdentityParts'

type CameraState = 'idle' | 'starting' | 'scanning' | 'denied' | 'policy' | 'failed'
type Reader = (video: HTMLVideoElement) => Promise<string[]>

interface NativeDetector { detect(source: HTMLVideoElement): Promise<Array<{ rawValue: string }>> }
interface NativeDetectorConstructor { new (options: { formats: string[] }): NativeDetector; getSupportedFormats?: () => Promise<string[]> }

/**
 * Decodes with the browser's `BarcodeDetector` where it supports QR codes (Chrome on Android); anywhere else
 * (Safari on iPhone included) the pure-JavaScript jsQR is imported at this moment and no earlier. A wasm decoder
 * would need `'wasm-unsafe-eval'` in the CSP, which Vessel does not grant.
 */
async function createReader(): Promise<Reader> {
  const Native = (window as unknown as { BarcodeDetector?: NativeDetectorConstructor }).BarcodeDetector
  if (Native) {
    try {
      const formats = await Native.getSupportedFormats?.()
      if (!formats || formats.includes('qr_code')) {
        const detector = new Native({ formats: ['qr_code'] })
        return async video => (await detector.detect(video)).map(code => code.rawValue)
      }
    } catch { /* The decoder below still works. */ }
  }
  const { default: jsQR } = await import('jsqr')
  const canvas = document.createElement('canvas')
  return async video => {
    const width = video.videoWidth, height = video.videoHeight
    if (!width || !height) return []
    // A downscaled frame keeps the decoder quick on a phone and loses nothing a QR code needs.
    const scale = Math.min(1, 640 / Math.max(width, height))
    canvas.width = Math.round(width * scale)
    canvas.height = Math.round(height * scale)
    const context = canvas.getContext('2d', { willReadFrequently: true })
    if (!context) return []
    context.drawImage(video, 0, 0, canvas.width, canvas.height)
    const frame = context.getImageData(0, 0, canvas.width, canvas.height)
    const code = jsQR(frame.data, frame.width, frame.height, { inversionAttempts: 'dontInvert' })
    return code ? [code.data] : []
  }
}

/**
 * False when the page's Permissions-Policy (sent by Vessel or a proxy in front of it) forbids the camera. The browser
 * then refuses without asking, so no browser setting helps. Only Chromium-based browsers can tell; elsewhere null.
 */
function cameraAllowedByPolicy(): boolean | null {
  const policy = (document as unknown as { featurePolicy?: { allowsFeature(feature: string): boolean } }).featurePolicy
  try { return policy ? policy.allowsFeature('camera') : null } catch { return null }
}

const scanEvery = 250
const noticeFor = 3_000
const foreignCodeMessage = 'This code is for a different Vessel.'

interface PairingScannerProps {
  /** A scanned pairing link must be on this origin. */
  origin?: string
  onSecret: (secret: string) => void
  onType: () => void
  onBack: () => void
}

/**
 * Sign-in screen → Scan a code from your desktop. The camera starts on the tap, prefers the rear one and stops the
 * moment a code is read, on Back, on unmount and whenever the page is hidden. Frames stay in this page: only the
 * secret in the fragment of an accepted link goes anywhere, to `claim`.
 */
export function PairingScanner({ origin = window.location.origin, onSecret, onType, onBack }: PairingScannerProps) {
  const video = useRef<HTMLVideoElement>(null)
  const active = useRef<{ stream: MediaStream | null; stopped: boolean; timer: ReturnType<typeof setTimeout> | undefined } | null>(null)
  const [camera, setCamera] = useState<CameraState>('idle')
  const [notice, setNotice] = useState<string | null>(null)
  const secretHandler = useRef(onSecret)
  useEffect(() => { secretHandler.current = onSecret }, [onSecret])

  const stop = useCallback(() => {
    const current = active.current
    active.current = null
    if (!current) return
    current.stopped = true
    clearTimeout(current.timer)
    current.stream?.getTracks().forEach(track => track.stop())
    if (video.current) video.current.srcObject = null
  }, [])
  useEffect(() => {
    const hidden = () => {
      if (document.visibilityState === 'hidden' && active.current) { stop(); setCamera('idle') }
    }
    document.addEventListener('visibilitychange', hidden)
    return () => { document.removeEventListener('visibilitychange', hidden); stop() }
  }, [stop])

  async function start() {
    stop()
    setNotice(null)
    if (cameraAllowedByPolicy() === false) { setCamera('policy'); return }
    setCamera('starting')
    const current: NonNullable<typeof active.current> = { stream: null, stopped: false, timer: undefined }
    active.current = current
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false })
      if (current.stopped) { stream.getTracks().forEach(track => track.stop()); return }
      current.stream = stream
      const element = video.current
      if (!element) throw new Error('no preview')
      element.srcObject = stream
      await element.play()
      const read = await createReader()
      if (current.stopped) return
      setCamera('scanning')
      let lastForeign = 0
      const scan = async () => {
        if (current.stopped || !video.current) return
        try {
          for (const text of await read(video.current)) {
            if (current.stopped) return
            const result = classifyScan(text, origin)
            if (result.kind === 'secret') {
              // The camera is off before anything else happens with the secret.
              stop()
              setCamera('idle')
              secretHandler.current(result.secret)
              return
            }
            if (result.kind === 'foreign') { lastForeign = Date.now(); setNotice(foreignCodeMessage) }
          }
        } catch { /* A frame that cannot be read is skipped. */ }
        if (current.stopped) return
        if (lastForeign && Date.now() - lastForeign > noticeFor) { lastForeign = 0; setNotice(null) }
        current.timer = setTimeout(() => { void scan() }, scanEvery)
      }
      void scan()
    } catch (cause) {
      if (current.stopped) return
      stop()
      const name = cause instanceof DOMException ? cause.name : ''
      setCamera(name === 'NotAllowedError' || name === 'SecurityError' ? 'denied' : 'failed')
    }
  }

  const running = camera === 'starting' || camera === 'scanning'
  return <>
    <div className="sign-in-head"><SignInBrand /></div>
    <h1 id="sign-in-title">Scan the code</h1>
    <p className="sign-in-lede">Hold the code from your computer in view. Nothing is recorded or uploaded.</p>
    <div className="sign-in-body">
      <div className={`pairing-camera${camera === 'scanning' ? ' is-live' : ''}`}>
        <video ref={video} playsInline muted aria-label="Camera preview" />
        {camera !== 'scanning' && <span className="pairing-camera-idle" aria-hidden="true">{camera === 'denied' || camera === 'policy' || camera === 'failed' ? <CameraOff size={28} /> : <Camera size={28} />}</span>}
      </div>
      <div aria-live="polite">
        {camera === 'starting' && <p className="muted">Starting the camera…</p>}
        {notice && <p className="sign-in-notice" role="status">{notice}</p>}
        {camera === 'denied' && <p className="error" role="alert">Camera access is blocked for this site. Allow it in your browser settings, or scan the code with your phone’s camera app.</p>}
        {camera === 'policy' && <p className="error" role="alert">This site does not allow the camera, so your browser cannot ask for it. Scan the code with your phone’s camera app, or enter a code instead.</p>}
        {camera === 'failed' && <p className="error" role="alert">The camera could not start. Try again, or enter a code instead.</p>}
      </div>
      <div className="sign-in-actions">
        {!running && camera !== 'policy' && <Button size="lg" className="sign-in-primary" onClick={() => { void start() }}><Camera size={16} aria-hidden="true" />
          {camera === 'denied' || camera === 'failed' ? 'Try the camera again' : 'Start camera'}</Button>}
        <Button size="lg" variant="outline" onClick={onType}><Keyboard size={16} aria-hidden="true" />Enter a code instead</Button>
        <Button size="lg" variant="ghost" onClick={onBack}>Back</Button>
      </div>
    </div>
  </>
}
