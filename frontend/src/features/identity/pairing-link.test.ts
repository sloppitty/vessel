import { afterEach, describe, expect, it, vi } from 'vitest'
import { cameraUsable, classifyScan, groupedCode, isLoopbackOrigin, pairingUrl, takePairingSecret } from './pairing-link'

const secret = 'Kq9-_x'.padEnd(43, 'a')
const origin = 'https://vessel.test'

describe('takePairingSecret', () => {
  it('reads the secret and removes the fragment from the address at once', () => {
    window.history.replaceState(null, '', `/overview?tab=1#pair=${secret}`)
    expect(takePairingSecret()).toBe(secret)
    expect(window.location.hash).toBe('')
    expect(window.location.pathname + window.location.search).toBe('/overview?tab=1')
  })

  it('leaves any other fragment alone', () => {
    window.history.replaceState(null, '', '/#plays')
    expect(takePairingSecret()).toBeNull()
    expect(window.location.hash).toBe('#plays')
    window.history.replaceState(null, '', '/')
    expect(takePairingSecret()).toBeNull()
  })

  it('removes a malformed secret too and returns nothing', () => {
    window.history.replaceState(null, '', '/#pair=short')
    expect(takePairingSecret()).toBeNull()
    expect(window.location.hash).toBe('')
  })

  it('builds the link the QR carries with the secret in the fragment only', () => {
    const url = new URL(pairingUrl(origin, secret))
    expect(url.search).toBe('')
    expect(url.hash).toBe(`#pair=${secret}`)
    expect(url.origin).toBe(origin)
  })
})

describe('classifyScan', () => {
  it('accepts a pairing link on this origin', () => {
    expect(classifyScan(pairingUrl(origin, secret), origin)).toEqual({ kind: 'secret', secret })
    expect(classifyScan(`${origin}/overview#pair=${secret}`, origin)).toEqual({ kind: 'secret', secret })
  })

  it('refuses a pairing link for another origin or port', () => {
    expect(classifyScan(pairingUrl('https://other.test', secret), origin)).toEqual({ kind: 'foreign' })
    expect(classifyScan(pairingUrl('https://vessel.test:8443', secret), origin)).toEqual({ kind: 'foreign' })
    expect(classifyScan(pairingUrl('http://vessel.test', secret), origin)).toEqual({ kind: 'foreign' })
  })

  it.each([
    ['not a URL', 'hello'],
    ['a web page', 'https://example.com/menu'],
    ['this origin without a fragment', origin],
    ['a different fragment', `${origin}/#plays`],
    ['a malformed secret', `${origin}/#pair=nope`],
    ['an empty secret', `${origin}/#pair=`],
    ['wifi credentials', 'WIFI:S:home;T:WPA;P:secret;;'],
  ])('ignores %s', (_label, text) => {
    expect(classifyScan(text, origin)).toEqual({ kind: 'ignore' })
  })
})

describe('loopback origins', () => {
  it.each(['http://localhost:5180', 'http://127.0.0.1:5180', 'http://[::1]:5180', 'http://127.1.2.3', 'http://app.localhost'])('%s is loopback', value => {
    expect(isLoopbackOrigin(value)).toBe(true)
  })
  it.each(['https://vessel.test', 'http://192.168.1.20:5180', 'https://vessel.tail1234.ts.net', 'nonsense'])('%s is reachable from a phone', value => {
    expect(isLoopbackOrigin(value)).toBe(false)
  })
})

describe('cameraUsable', () => {
  afterEach(() => vi.unstubAllGlobals())
  const configure = (secure: boolean, mediaDevices: unknown) => {
    Object.defineProperty(window, 'isSecureContext', { configurable: true, value: secure })
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: mediaDevices })
  }
  it('needs a secure context and getUserMedia', () => {
    configure(true, { getUserMedia: vi.fn() })
    expect(cameraUsable()).toBe(true)
    configure(false, { getUserMedia: vi.fn() })
    expect(cameraUsable()).toBe(false)
    configure(true, undefined)
    expect(cameraUsable()).toBe(false)
    configure(true, {})
    expect(cameraUsable()).toBe(false)
  })
})

it('groups the confirmation code', () => expect(groupedCode('482913')).toBe('482 913'))
