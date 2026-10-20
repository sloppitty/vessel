// Browser part of scripts/e2e-full-stack.py: the production web image against the real API, worker and fake
// venue, with no route interception. Runs in the pinned Playwright image; prints PASS/FAIL [browser] lines.
// E2E_PHASE=live: monitor panel, discrepancies, SSE live updates, Check 202/cooldown/429, dismissal, a Play
// closing on screen. E2E_PHASE=ended: degraded badge, watched orders and unverifiable records.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE)
const assert = require('node:assert/strict')
const path = require('node:path')

const base = process.env.E2E_URL, token = process.env.E2E_TOKEN, phase = process.env.E2E_PHASE
const ids = JSON.parse(process.env.E2E_IDS), out = process.env.E2E_OUT
let failed = 0
const pass = label => console.log(`PASS [browser] ${label}`)
async function step(label, body) {
  try { await body(); pass(label) } catch (error) { failed++; console.log(`FAIL [browser] ${label} -- ${String(error.message || error).split('\n')[0]}`); throw error }
}
const api = (route, init = {}) => fetch(base + route, { ...init, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...(init.headers || {}) } })
const control = (route, body) => fetch(`${process.env.E2E_CONTROL}/control/${route}`, { method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } })
  .then(r => { assert.equal(r.status, 200); return r.json() })

async function main() {
  const browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ viewport: { width: 1512, height: 1000 } })
  // Test instrumentation only: tee the app's own authenticated SSE response so frames can be asserted.
  await context.addInitScript(() => {
    window.__sse = []
    const original = window.fetch
    window.fetch = async (input, init) => {
      const response = await original(input, init)
      const url = typeof input === 'string' ? input : input.url
      if (!url.includes('/api/events/stream') || !response.body) return response
      const [app, copy] = response.body.tee()
      ;(async () => {
        const reader = copy.getReader(), decoder = new TextDecoder()
        let text = ''
        try {
          for (;;) {
            const { value, done } = await reader.read()
            if (done) break
            text += decoder.decode(value, { stream: true })
            let index
            while ((index = text.indexOf('\n\n')) >= 0) { window.__sse.push(text.slice(0, index)); text = text.slice(index + 2) }
          }
        } catch { /* aborted with the app's stream */ }
      })()
      return new Response(app, { status: response.status, statusText: response.statusText, headers: response.headers })
    }
  })
  const page = await context.newPage()
  page.setDefaultTimeout(30_000)
  const errors = [], violations = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  await page.exposeFunction('recordCspViolation', entry => violations.push(entry))
  await page.addInitScript(() => document.addEventListener('securitypolicyviolation', event => window.recordCspViolation({ directive: event.violatedDirective, blocked: event.blockedURI })))
  await page.route(url => url.origin !== new URL(base).origin, route => { errors.push('external request ' + route.request().url()); return route.abort() })
  const shot = name => page.screenshot({ path: path.join(out, `${phase}-${name}.png`), fullPage: true })
  const sseFrames = () => page.evaluate(() => window.__sse.slice())
  const sseHas = async (type, predicate = () => true) => (await sseFrames()).some(frame => {
    const data = frame.split('\n').find(line => line.startsWith('data: '))
    if (!frame.includes('event: event') || !data) return false
    const event = JSON.parse(data.slice(6))
    return event.type === type && predicate(event)
  })
  const openPlay = async title => {
    await page.getByRole('link', { name: 'Plays', exact: true }).click()
    const back = page.getByRole('button', { name: 'All plays', exact: true })
    if (await back.count()) await back.click()
    // The list starts on "In progress"; closed and cancelled Plays are under the "All" tab (its name carries a count).
    await page.getByRole('tablist', { name: 'Show plays', exact: true }).getByRole('tab', { name: /^All\b/ }).click()
    await page.getByRole('button', { name: `Open ${title}`, exact: true }).click()
    await page.getByRole('tab', { name: 'Execution', exact: true }).click()
    return page.getByRole('region', { name: 'Background monitor', exact: true })
  }
  const issue = kind => page.getByRole('region', { name: 'Monitor discrepancies', exact: true }).getByRole('listitem').filter({ hasText: kind })
  const statusPill = () => page.locator('.plays-context-status .badge').first()

  try {
    await step('login with the generated token over the packaged web image', async () => {
      await page.goto(base)
      await page.getByLabel('API token').fill(token)
      await page.getByRole('button', { name: 'Connect', exact: true }).click()
      await page.getByRole('region', { name: 'Overview summary', exact: true }).waitFor()
    })
    if (phase === 'live') {
      await step('no worker-offline banner while the worker heartbeat is fresh', async () => {
        await page.waitForTimeout(1000)
        assert.equal(await page.locator('.monitor-offline').count(), 0)
      })
      await step('account detail shows the live monitor and both open critical discrepancies', async () => {
        await page.getByRole('link', { name: 'Accounts', exact: true }).click()
        await page.getByRole('button', { name: `View ${ids.accountName}`, exact: true }).click()
        const panel = page.getByRole('region', { name: 'Background monitor', exact: true })
        // Open critical records head the panel; it never reads "Monitored" beside them.
        await panel.getByText(/^Needs attention · \d+ critical/).waitFor()
        assert.equal(await panel.getByText('Monitored', { exact: true }).count(), 0)
        for (const kind of ['The stop was cancelled · BTC', 'No stop protects the open position · ETH']) await issue(kind).getByText('critical · open', { exact: true }).waitFor()
        await issue('The stop was cancelled').getByText('Next:', { exact: false }).waitFor()
        await issue('The stop was cancelled').getByText('Recorded details', { exact: true }).click()
        await issue('The stop was cancelled').getByText('"stopQuantity": "0.5"', { exact: false }).waitFor()
        await shot('account-detail')
      })
      let panel
      await step('Play execution tab shows the monitor panel and its discrepancy', async () => {
        panel = await openPlay('E2E P1 BTC long')
        await panel.getByText(/^Needs attention · \d+ critical/).waitFor()
        await page.getByRole('button', { name: /^Needs attention · \d+ critical/ }).waitFor()
        await issue('The stop was cancelled').getByText('critical · open', { exact: true }).waitFor()
        assert.equal(await statusPill().innerText(), 'Open')
        await shot('p1-execution')
      })
      await step('a dismissal from another client reaches the open page through SSE without reload', async () => {
        const navigations = []
        page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations.push(frame.url()) })
        const response = await api(`/api/plays/${ids.p1}/discrepancies/${ids.p1_cancelled}/dismiss`, { method: 'POST', body: JSON.stringify({ note: 'Dismissed from a second client' }) })
        assert.equal(response.status, 204)
        await issue('The stop was cancelled').getByText('critical · dismissed', { exact: true }).waitFor({ timeout: 20_000 })
        await issue('The stop was cancelled').getByText('Dismissed from a second client', { exact: true }).waitFor()
        assert.ok(await sseHas('discrepancy.resolved', e => e.subject.discrepancyId === ids.p1_cancelled && e.data.resolution === 'dismissed'))
        assert.deepEqual(navigations, [])
      })
      await step('owner dismissal in the UI keeps its note and explains that checks continue', async () => {
        await openPlay('E2E P2 ETH long')
        const item = issue('No stop protects the open position')
        await item.getByText('critical · open', { exact: true }).waitFor()
        await item.getByRole('textbox', { name: 'Dismissal note: No stop protects the open position · ETH' }).fill('Reviewed in the end-to-end browser test')
        await item.getByRole('button', { name: 'Dismiss', exact: true }).click()
        await item.getByText('critical · dismissed', { exact: true }).waitFor()
        await item.getByText('Reviewed in the end-to-end browser test', { exact: true }).waitFor()
        await page.getByText('It does not change executions or stop checks', { exact: false }).waitFor()
        assert.equal(await item.getByRole('button', { name: 'Dismiss', exact: true }).count(), 0)
      })
      const button = () => page.getByRole('region', { name: 'Background monitor', exact: true }).getByRole('button').first()
      const idle = () => page.getByRole('region', { name: 'Background monitor', exact: true }).getByRole('button', { name: 'Check', exact: true }).waitFor({ timeout: 90_000 })
      await step('Check is accepted (202), shows progress, completes via the stream and then counts down', async () => {
        await idle()
        const [response] = await Promise.all([
          page.waitForResponse(r => r.url().endsWith(`/api/accounts/${ids.account}/monitor/run`) && r.request().method() === 'POST'),
          button().click(),
        ])
        assert.equal(response.status(), 202)
        assert.ok(await button().isDisabled())
        await page.getByRole('region', { name: 'Background monitor', exact: true }).getByRole('button', { name: /^Check in \d+s$/ }).waitFor({ timeout: 60_000 })
        assert.ok(await button().isDisabled())
        const body = await response.json()
        await page.waitForFunction(id => window.__sse.some(f => f.includes('"monitor.run-completed"') && f.includes(id)), body.requestId, { timeout: 30_000 })
        await shot('p2-check-cooldown')
      })
      await step('a Check refused while another client runs one shows the server 429 explanation', async () => {
        await idle()
        const other = await api(`/api/accounts/${ids.account}/monitor/run`, { method: 'POST' })
        assert.equal(other.status, 202)
        const [response] = await Promise.all([
          page.waitForResponse(r => r.url().endsWith(`/api/accounts/${ids.account}/monitor/run`) && r.request().method() === 'POST'),
          button().click(),
        ])
        assert.equal(response.status(), 429)
        const alert = page.getByRole('region', { name: 'Background monitor', exact: true }).getByRole('alert')
        await alert.waitFor()
        const text = (await alert.innerText()).trim()
        assert.ok(text.length > 10, text)
        console.log(`      [browser] 429 message: ${text}`)
        await shot('p2-check-429')
      })
      await step('a venue fill closes the open Play on screen after Check, without reload', async () => {
        await openPlay('E2E P1 BTC long')
        assert.equal(await statusPill().innerText(), 'Open')
        await control('fill', { address: ids.address, oid: ids.p1_target })
        await idle()
        await button().click()
        await page.locator('.plays-context-status .badge', { hasText: 'Closed' }).waitFor({ timeout: 90_000 })
        assert.ok(await sseHas('play.closed', e => e.subject.playId === ids.p1))
        await page.getByRole('region', { name: 'Watched orders', exact: true }).getByRole('listitem')
          .filter({ hasText: `Order ${ids.p1_stop2}` }).getByText('Watching', { exact: true }).waitFor()
        await shot('p1-closed')
      })
      await step('Paper theme and 390px layout keep the monitor panel usable without horizontal overflow', async () => {
        await page.getByRole('link', { name: 'Settings', exact: true }).click()
        await page.getByRole('button', { name: /^Paper(\s|$)/ }).click()
        const panel = await openPlay('E2E P2 ETH long')
        await shot('p2-paper')
        await page.setViewportSize({ width: 390, height: 844 })
        await panel.scrollIntoViewIfNeeded()
        assert.ok(await panel.isVisible())
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth))
        await shot('p2-mobile')
        await page.setViewportSize({ width: 1512, height: 1000 })
        await page.getByRole('link', { name: 'Settings', exact: true }).click()
        await page.getByRole('button', { name: /^Graphite(\s|$)/ }).click()
      })
    } else {
      await step('account row and detail badge the degraded monitor with its safe reason', async () => {
        await page.getByRole('link', { name: 'Accounts', exact: true }).click()
        await page.locator('.monitor-badge', { hasText: /Monitor (degraded|stale)/ }).first().waitFor()
        await page.getByRole('button', { name: `View ${ids.accountName}`, exact: true }).click()
        await page.getByRole('region', { name: 'Account summary', exact: true }).locator('.monitor-badge', { hasText: /Monitor (degraded|stale)/ }).waitFor()
        const panel = page.getByRole('region', { name: 'Background monitor', exact: true })
        await panel.getByText(/^Monitor (degraded|stale)$/).waitFor()
        await panel.locator('.monitor-warning').first().waitFor()
        await shot('account-degraded')
      })
      await step('an unverifiable discrepancy renders as unverifiable, still dismissable', async () => {
        const item = issue('An entry order still rests after the Play ended')
        await item.getByText('critical · unverifiable', { exact: true }).waitFor()
        await item.getByText('It is not resolved and is no longer checked', { exact: false }).waitFor()
        assert.equal(await item.getByRole('button', { name: 'Dismiss', exact: true }).count(), 1)
      })
      await step('closed Play shows its leftover stop watch as persistently unverifiable', async () => {
        await openPlay('E2E P1 BTC long')
        assert.equal(await statusPill().innerText(), 'Closed')
        const watches = page.getByRole('region', { name: 'Watched orders', exact: true })
        const stop = watches.getByRole('listitem').filter({ hasText: `Order ${ids.p1_stop2}` })
        await stop.getByText('Unverifiable', { exact: true }).waitFor()
        await stop.getByText('The watch lifetime passed without venue proof', { exact: false }).waitFor()
        assert.ok(await watches.getByText('No longer resting', { exact: true }).count() >= 1)
        await issue('A stop or target still rests after the Play ended').getByText('warning · open', { exact: true }).waitFor()
        await shot('p1-watch-unverifiable')
      })
      await step('cancelled Play shows its resting entry watched and the orphaned entry', async () => {
        await openPlay('E2E P3 AVAX long')
        assert.equal(await statusPill().innerText(), 'Cancelled')
        const watches = page.getByRole('region', { name: 'Watched orders', exact: true })
        await watches.getByRole('listitem').filter({ hasText: `Order ${ids.p3_entry}` }).getByText('Watching', { exact: true }).waitFor()
        await issue('An entry order still rests after the Play ended').waitFor()
        await shot('p3-cancelled')
      })
    }
    await step('zero console errors, page errors, external requests and CSP violations', async () => {
      assert.deepEqual(violations, [], JSON.stringify(violations))
      // Chromium logs every non-2xx fetch; only the one deliberately provoked 429 is expected.
      const provoked = errors.filter(e => /status of 429 \(Too Many Requests\)/.test(e))
      assert.ok(provoked.length <= (phase === 'live' ? 1 : 0), JSON.stringify(provoked))
      assert.deepEqual(errors.filter(e => !provoked.includes(e)), [], JSON.stringify(errors))
    })
  } finally {
    await browser.close()
  }
}

main().then(() => { process.exitCode = failed ? 1 : 0 }, error => { if (!failed) console.log(`FAIL [browser] ${phase} phase -- ${error.message}`); process.exitCode = 1 })
