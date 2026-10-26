// Production-image CSP check against verify-release.py's isolated synthetic API.
// Playwright and the signer's NOBLE_MODULE_DIR are supplied externally, never installed as application dependencies.
const assert = require('node:assert/strict')
const { signer, exposeSyntheticWallet, signInWithSyntheticWallet } = require('./wallet-test-signer.cjs')

function unexpectedConsoleError(message, signedIn) {
  return message.type() === 'error' && (signedIn || !/^Failed to load resource: the server responded with a status of 401 \((?:Unauthorized)?\)$/.test(message.text()))
}

async function main() {
  assert.equal(process.env.VESSEL_RELEASE_SYNTHETIC, '1')
  assert.ok(process.env.VESSEL_RELEASE_URL && process.env.VESSEL_RELEASE_CHART_ACCOUNT)
  const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright')
  // The synthetic operator wallet verify-release.py bound with `identity bind-wallet`.
  const operator = await signer(1)
  const browser = await chromium.launch({ headless: true, ...(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}) })
  const failures = [], violations = [], seen = new Set()
  let checks = 0
  const pass = label => { checks++; console.log(`PASS [browser] ${label}`) }
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1512, height: 1120 } })
    await exposeSyntheticWallet(context, operator)
    const page = await context.newPage()
    const verifyMonitor = await require('./monitor-browser-smoke.cjs')(page, process.env.VESSEL_RELEASE_CHART_ACCOUNT)
    await page.exposeFunction('recordCspViolation', entry => violations.push(entry))
    await page.addInitScript(() => document.addEventListener('securitypolicyviolation', event => window.recordCspViolation({ directive: event.violatedDirective, blocked: event.blockedURI })))
    // Before sign-in, the session probe answers 401. Chromium omits its reason phrase over HTTP/2.
    let signedIn = false
    page.on('console', message => {
      if (unexpectedConsoleError(message, signedIn)) failures.push(message.text())
    })
    page.on('pageerror', error => failures.push(error.message))
    // The app still uses its real same-origin fetch/chart/canvas code. Intercept
    // public market data only, to avoid all outbound venue requests in this check.
    const start = Math.floor(Date.now() / 3600000) * 3600000 - 250 * 3600000
    const candles = Array.from({ length: 250 }, (_, i) => ({ openTime: start + i * 3600000, closeTime: start + (i + 1) * 3600000 - 1, open: String(100 + i / 10), high: String(102 + i / 10), low: String(99 + i / 10), close: String(101 + i / 10), volume: '12.5', trades: 40 }))
    const market = { venueId: 'hyperliquid', instrument: 'BTC', markPrice: '125.9', oraclePrice: '125.8', midPrice: '125.85', previousDayPrice: '100', dayNotionalVolume: '1234567.891', openInterest: '42.5', fundingRate: '0.0000125', premium: '-0.0001', observedAt: new Date().toISOString(), notice: 'Synthetic CSP verification only.' }
    await page.route(/\/api\/accounts\/[^/]+\/(instruments|candles|market-context|market-stream)(\?|$)/, async route => {
      const url = new URL(route.request().url()), kind = url.pathname.split('/').at(-1)
      seen.add(kind)
      if (kind === 'market-stream') {
        return route.fulfill({ status: 200, contentType: 'text/event-stream', body: `event: status\ndata: ${JSON.stringify({ state: 'live', observedAt: new Date().toISOString() })}\n\n` })
      }
      const value = kind === 'instruments'
        ? { venueId: 'hyperliquid', marketScope: 'perpetuals', scope: 'venue-perpetuals', instruments: [{ contractId: 'BTC', quantityDecimals: 5, maxLeverage: 40, quoteAsset: 'USDC' }], notice: 'Synthetic CSP verification only.' }
        : kind === 'market-context' ? market
          : { venueId: 'hyperliquid', instrument: 'BTC', interval: url.searchParams.get('interval'), priceSource: 'trades', candles, requestedFrom: start, requestedTo: start + 250 * 3600000, retrievedAt: new Date().toISOString(), historyExhausted: true, notice: 'Synthetic CSP verification only.' }
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(value) })
    })
    // Any unexpected external browser connection fails rather than calling a venue.
    await page.route(url => url.origin !== new URL(process.env.VESSEL_RELEASE_URL).origin, route => { failures.push('Unexpected external request: ' + new URL(route.request().url()).origin); return route.abort() })
    await signInWithSyntheticWallet(page, process.env.VESSEL_RELEASE_URL)
    signedIn = true
    assert.ok(await page.getByRole('region', { name: 'Scoped overview totals' }).isVisible())
    pass('operator wallet sign-in and new Overview summary use real synthetic API data')
    await page.evaluate(() => document.fonts.ready)
    assert.ok(await page.evaluate(() => [...document.fonts].some(font => font.status === 'loaded')))
    pass('self-hosted fonts load')
    await page.getByRole('link', { name: 'Plays', exact: true }).click()
    await page.getByRole('button', { name: 'New play', exact: true }).click()
    await page.locator('.plays-account-field .account-picker-trigger').click()
    await page.locator(`.account-picker-option[data-value="${process.env.VESSEL_RELEASE_CHART_ACCOUNT}"]`).click()
    const instrument = page.getByRole('combobox', { name: 'Perpetual instrument' })
    await instrument.fill('BTC')
    await instrument.press('Enter')
    await page.locator('.candle-chart canvas').first().waitFor()
    await page.getByRole('button', { name: 'Capture chart', exact: true }).waitFor({ state: 'visible' })
    await page.getByRole('button', { name: 'Capture chart', exact: true }).click()
    await page.getByText('Capture added to the Evidence tab of the journal.', { exact: false }).waitFor()
    pass('Lightweight Charts lazy script, canvas, indicators and chart capture')
    // Capture switches to evidence automatically only on explicit Show.
    const show = page.getByRole('button', { name: 'Show', exact: true })
    if (await show.count()) await show.click()
    else await page.getByRole('tab', { name: 'Evidence', exact: true }).click()
    await page.locator('.evidence-thumb').first().waitFor()
    // Object URLs are acquired after React commits; an attached img can still have no src.
    assert.ok(await page.locator('.evidence-thumb img[src^="blob:"]').first().evaluate(async img => { await img.decode(); return img.naturalWidth > 0 && img.src.startsWith('blob:') }))
    pass('blob chart capture image renders')
    await page.locator('.evidence-thumb').first().click()
    await page.getByRole('button', { name: 'Mark up', exact: true }).click()
    await page.getByRole('button', { name: 'Box', exact: true }).click()
    const stage = page.locator('.markup-stage')
    const box = await stage.boundingBox()
    assert.ok(box)
    await page.mouse.move(box.x + box.width * .25, box.y + box.height * .25)
    await page.mouse.down()
    await page.mouse.move(box.x + box.width * .7, box.y + box.height * .7, { steps: 8 })
    await page.mouse.up()
    await page.getByRole('button', { name: 'Done', exact: true }).click()
    await page.getByRole('button', { name: 'Marked', exact: true }).waitFor()
    assert.ok(await page.locator('.evidence-dialog-image img[src^="blob:"]').first().evaluate(async img => { await img.decode(); return img.naturalWidth > 0 }))
    assert.ok(await page.locator('.evidence-dialog-image .marked-image-overlay').evaluate(async img => { await img.decode(); return img.naturalWidth > 0 && img.src.startsWith('data:') }))
    pass('vector markup and marked data-URL overlay render')
    await page.getByRole('button', { name: 'Close', exact: true }).click()
    const image = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
    await page.locator('.evidence-panel input[type=file]').setInputFiles({ name: 'synthetic.png', mimeType: 'image/png', buffer: image })
    await page.waitForFunction(() => document.querySelectorAll('.evidence-thumb').length === 2)
    assert.ok(await page.locator('.evidence-thumb').nth(1).locator('img[src^="blob:"]').first().evaluate(async img => { await img.decode(); return img.naturalWidth > 0 }))
    pass('image upload renders')
    // Exercise data: rendering explicitly as markup serialization uses data URLs.
    assert.ok(await page.evaluate(async () => {
      const img = new Image()
      img.src = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='
      await img.decode()
      return img.naturalWidth === 1
    }))
    pass('data image URLs permitted')
    await page.getByRole('link', { name: 'Settings', exact: true }).click()
    await page.getByRole('heading', { name: 'Theme builder', exact: true }).waitFor()
    const themeCards = page.locator('.theme-card')
    for (let i = 0; i < await themeCards.count(); i++) await themeCards.nth(i).click()
    await page.getByRole('button', { name: 'Randomize', exact: true }).click()
    await page.getByRole('textbox', { name: 'Theme name', exact: true }).fill('Release CSP synthetic')
    await page.getByRole('button', { name: 'Save theme', exact: true }).click()
    assert.ok(await page.getByRole('button', { name: /Release CSP synthetic/ }).count())
    pass('built-in light/dark themes, inline styles and named theme builder')
    assert.ok(seen.has('market-stream'))
    pass('same-origin SSE fetch passes CSP connect-src')
    await verifyMonitor(pass)
    assert.deepEqual(violations, [], 'CSP violations: ' + JSON.stringify(violations))
    assert.deepEqual(failures, [], 'Browser errors: ' + JSON.stringify(failures))
    pass('zero CSP violations and zero browser console/page errors')
    console.log(`Browser checks: ${checks}; CSP violations: ${violations.length}; console/page errors: ${failures.length}`)
  } finally {
    if (violations.length || failures.length) console.error(JSON.stringify({ violations, failures }))
    await browser.close()
  }
}
module.exports = { unexpectedConsoleError }
if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1 })
