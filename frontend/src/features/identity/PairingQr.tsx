import { useMemo } from 'react'
import { encode } from 'uqr'

const quietZone = 4

/**
 * The pairing link as a QR code, drawn as inline SVG (no image, so the CSP needs nothing).
 *
 * Deliberate exception to the theme tokens: the code is always black on white with a four-module quiet zone,
 * whatever the theme, because phone cameras and decoders expect dark modules on a light background.
 */
export function PairingQr({ value, label }: { value: string; label: string }) {
  const { size, path } = useMemo(() => {
    const { data, size: modules } = encode(value, { ecc: 'M', border: 0 })
    let d = ''
    data.forEach((row, y) => {
      for (let x = 0; x < modules; x++) {
        if (!row[x]) continue
        let end = x
        while (end + 1 < modules && row[end + 1]) end++
        d += `M${x + quietZone} ${y + quietZone}h${end - x + 1}v1h-${end - x + 1}z`
        x = end
      }
    })
    return { size: modules + quietZone * 2, path: d }
  }, [value])
  return <svg className="pairing-qr" viewBox={`0 0 ${size} ${size}`} role="img" aria-label={label} shapeRendering="crispEdges">
    <rect width={size} height={size} fill="#ffffff" />
    <path d={path} fill="#000000" />
  </svg>
}
