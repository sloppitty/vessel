// Additional browser pass against a KEPT scripts/e2e-full-stack.py stack, with no route interception.
// Supply E2E_URL, E2E_TOKEN (from that run's generated .env), E2E_IDS, E2E_OUT and PLAYWRIGHT_MODULE.
// E2E_IDS needs the suite's account/portfolio IDs and accountName: "E2E Hyperliquid".
// Run with the packaged suite's pinned Playwright image/module and host networking.
// Creates synthetic Paper records through the UI. Never use against an owner/development installation.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE)
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const base = process.env.E2E_URL, token = process.env.E2E_TOKEN, out = process.env.E2E_OUT
const ids = JSON.parse(process.env.E2E_IDS || '{}')
assert.ok(token && out && /^http:\/\/127\.0\.0\.1:\d+$/.test(base), 'An isolated loopback E2E run is required')
assert.ok(!['5080', '5180', '55432'].includes(new URL(base).port), 'Never target development services')
assert.ok(ids.accountName?.startsWith('E2E '), 'Synthetic account fixture required')
const results = [], errors = [], violations = [], external = [], screenshots = [], layouts = []
const started = Date.now()
const tag = started.toString(36)
const paperPortfolio = `E2E Shell paper portfolio ${tag}`, paperAccount = `E2E Shell paper account ${tag}`
const clean = value => String(value).split(token).join('[redacted]')
let page, context, browser
const crumb = () => page.getByRole('navigation', { name: 'Breadcrumb', exact: true })
const title = () => page.getByRole('textbox', { name: 'Play title', exact: true })
const switchName = mode => mode === 'real'
  ? 'Real journal, switch to Paper playground' : 'Paper playground, switch to Real journal'
const switchButton = mode => page.getByRole('button', { name: switchName(mode), exact: true }).filter({ visible: true })
const api = async route => {
  const response = await fetch(base + route, { headers: { Authorization: `Bearer ${token}` } })
  assert.equal(response.status, 200, `GET ${route}`)
  return response.json()
}
async function until(check, message, timeout = 15_000) {
  const end = Date.now() + timeout
  while (Date.now() < end) {
    if (await check()) return
    await page.waitForTimeout(100)
  }
  assert.fail(message)
}
async function shot(name) {
  const file = `shell-${name}.png`
  await page.screenshot({ path: path.join(out, file), fullPage: true })
  screenshots.push(file)
}
async function step(label, body) {
  const start = Date.now()
  try {
    await body()
    results.push({ label, result: 'PASS', seconds: (Date.now() - start) / 1000 })
    console.log(`PASS [shell] ${label}`)
    return true
  } catch (error) {
    const detail = clean(error.stack || error)
    results.push({ label, result: 'FAIL', detail, seconds: (Date.now() - start) / 1000 })
    console.log(`FAIL [shell] ${label} -- ${detail.split('\n')[0]}`)
    const name = `failure-${results.length}`
    await shot(name).catch(() => {})
    await fs.promises.writeFile(path.join(out, `shell-${name}.txt`), clean(await page.locator('body').innerText())).catch(() => {})
    return false
  }
}
async function trail(expected) {
  await until(async () => JSON.stringify(await crumb().locator(':scope > ol > li').allTextContents()) === JSON.stringify(expected),
    `Expected breadcrumb ${JSON.stringify(expected)}; got ${JSON.stringify(await crumb().locator(':scope > ol > li').allTextContents())}`)
  assert.equal(await crumb().locator('[aria-current="page"]').count(), 1)
}
async function openSheet() {
  if (!await page.locator('.shell-sidebar').evaluate(el => el.classList.contains('is-open'))) {
    await page.getByRole('button', { name: 'Open navigation', exact: true }).click()
  }
  await page.locator('.shell-sidebar.is-open').waitFor()
}
async function closeSheet() {
  if (await page.locator('.shell-sidebar.is-open').count()) {
    await page.keyboard.press('Escape')
    await page.locator('.shell-sidebar.is-open').waitFor({ state: 'detached' })
  }
}
async function nav(name) {
  if (await page.getByRole('button', { name: 'Open navigation', exact: true }).isVisible()
    && !(await page.locator('.shell-sidebar').evaluate(el => el.classList.contains('is-open')))) {
    await openSheet()
  }
  await page.getByRole('link', { name, exact: true }).filter({ visible: true }).click()
  await until(async () => new URL(page.url()).hash === `#${name.toLowerCase()}`, `Navigation to ${name}`)
}
async function header(paper = false) {
  assert.equal(await page.locator('header.shell-header').count(), 1)
  assert.equal(await page.locator('header.shell-header').evaluate(el => getComputedStyle(el).position), 'sticky')
  await crumb().waitFor()
  const notices = page.getByRole('note').filter({ hasText: 'Paper mode' })
  assert.equal(await notices.count(), paper ? 1 : 0)
  if (paper) {
    assert.equal(await page.locator('header.shell-header [role="note"]').count(), 1)
    assert.match(await notices.innerText(), /Zero fees and slippage; candle touches can be ambiguous/)
  }
}
async function noItemPaper() {
  // The explicitly decorative, aria-hidden narrow header mark is the one allowed stand-alone "Paper".
  const offenders = await page.locator('main, [role="dialog"]').evaluateAll(roots => roots.flatMap(root =>
    [...root.querySelectorAll('*')].filter(el => el.checkVisibility() && !el.children.length && /^(Paper|Paper · simulated)$/.test(el.textContent.trim()))
      .map(el => ({ tag: el.tagName, class: el.className, text: el.textContent }))))
  assert.deepEqual(offenders, [], `Redundant per-item Paper text: ${JSON.stringify(offenders)}`)
}
async function layout(width, paper, theme) {
  await page.setViewportSize({ width, height: 1000 })
  await page.waitForTimeout(200)
  await header(paper)
  const geometry = await page.evaluate(() => ({
    viewport: innerWidth, scroll: document.documentElement.scrollWidth,
    header: document.querySelector('.shell-header').getBoundingClientRect().toJSON(),
    notice: document.querySelector('.shell-notice')?.getBoundingClientRect().toJSON(),
    noticeLines: (() => {
      const span = document.querySelector('.shell-notice > span')
      return span ? span.getBoundingClientRect().height / parseFloat(getComputedStyle(span).lineHeight) : null
    })(),
  }))
  layouts.push({ width, paper, theme, ...geometry })
  assert.ok(geometry.scroll <= width, `Horizontal overflow: ${JSON.stringify(geometry)}`)
  if (paper && width === 1440) assert.ok(geometry.noticeLines <= 1.2, '1440px disclosure stays on one line')
  if (paper && width === 1024) assert.ok(geometry.noticeLines > 1.2, '1024px disclosure wraps without dropping copy')
  if (width <= 800 && paper) {
    const mark = page.locator('.header-mode')
    assert.ok(await mark.isVisible())
    assert.equal(await mark.getAttribute('aria-hidden'), 'true')
    assert.equal(await mark.locator('svg').count(), 1)
    assert.equal((await mark.innerText()).trim(), 'Paper')
    await openSheet()
    try {
      await switchButton('paper').waitFor()
      assert.equal(await switchButton('paper').count(), 1)
      await shot(`${theme}-${paper ? 'paper' : 'real'}-${width}-sheet`)
    } finally {
      await closeSheet()
    }
  }
  await shot(`${theme}-${paper ? 'paper' : 'real'}-${width}`)
  return geometry
}
async function main() {
  browser = await chromium.launch({ headless: true })
  context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' })
  page = await context.newPage()
  page.setDefaultTimeout(12_000)
  page.on('console', message => { if (message.type() === 'error') errors.push(clean(message.text())) })
  page.on('pageerror', error => errors.push(clean(error.message)))
  page.on('request', request => {
    if (new URL(request.url()).origin !== new URL(base).origin) external.push(clean(request.url()))
  })
  await page.exposeFunction('recordShellCsp', entry => violations.push(entry))
  await page.addInitScript(() => document.addEventListener('securitypolicyviolation',
    event => window.recordShellCsp({ directive: event.violatedDirective, blocked: event.blockedURI })))
  if (!await step('Login using the run-generated token, real API and packaged web image', async () => {
    assert.equal((await api(`/api/accounts/${ids.account}`)).name, ids.accountName, 'Verify synthetic fixture before browser writes')
    await page.goto(base)
    await page.getByLabel('API token').fill(token)
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    await page.getByRole('region', { name: 'Overview summary', exact: true }).waitFor()
    await header()
  })) return

  await step('Real: sticky header and breadcrumb on every top-level page; top-level resets; no Paper notice', async () => {
    for (const name of ['Overview', 'Review', 'Portfolios', 'Accounts', 'Activity', 'Notifications', 'Settings', 'Plays']) {
      await nav(name)
      await trail([name])
      await header()
    }
    await shot('real-pages')
  })
  await step('Real: Overview → account → Accounts crumb preserves account search, venue, status and portfolio filters', async () => {
    await nav('Accounts')
    await page.getByRole('textbox', { name: 'Search accounts', exact: true }).fill('Hyperliquid')
    await page.getByLabel('Filter by portfolio', { exact: true }).selectOption(ids.portfolio)
    await page.getByLabel('Filter by status', { exact: true }).selectOption('enabled')
    await page.getByRole('button', { name: 'Hyperliquid', exact: true }).click()
    await nav('Overview')
    await page.getByRole('button', { name: `View ${ids.accountName}`, exact: true }).click()
    await trail(['Overview', 'Accounts', ids.accountName])
    await shot('real-overview-account')
    await crumb().getByRole('link', { name: 'Accounts — previous location', exact: true }).click()
    await trail(['Overview', 'Accounts'])
    assert.equal(await page.getByRole('textbox', { name: 'Search accounts', exact: true }).inputValue(), 'Hyperliquid')
    assert.equal(await page.getByLabel('Filter by portfolio', { exact: true }).inputValue(), ids.portfolio)
    assert.equal(await page.getByLabel('Filter by status', { exact: true }).inputValue(), 'enabled')
    assert.equal(await page.getByRole('button', { name: 'Hyperliquid', exact: true }).getAttribute('aria-pressed'), 'true')
  })
  await step('Real: Accounts list → detail → All accounts pops; revisiting an earlier location truncates', async () => {
    await nav('Accounts')
    await page.getByRole('button', { name: `View ${ids.accountName}`, exact: true }).click()
    await trail(['Accounts', ids.accountName])
    await page.getByRole('button', { name: 'All accounts', exact: true }).click()
    await trail(['Accounts'])
    await page.getByRole('button', { name: `View ${ids.accountName}`, exact: true }).click()
    await crumb().getByRole('link', { name: 'Accounts — previous location', exact: true }).click()
    await trail(['Accounts'])
  })
  await step('Real: browser back/forward and address-bar hash navigation restore/reset the breadcrumb', async () => {
    await nav('Overview')
    await nav('Activity')
    await page.goBack()
    await trail(['Overview'])
    await page.goForward()
    await trail(['Activity'])
    await page.evaluate(() => { location.hash = '#review' })
    await trail(['Review'])
    await header()
  })
  await step('Real: Overview → saved Play adds target page and saved title', async () => {
    await nav('Overview')
    await page.getByRole('tablist', { name: 'Show overview plays', exact: true }).getByRole('tab', { name: /^All\b/ }).click()
    await page.getByRole('button', { name: 'Open E2E P1 BTC long', exact: true }).click()
    await title().waitFor()
    await trail(['Overview', 'Plays', 'E2E P1 BTC long'])
    await shot('real-overview-saved-play')
  })
  await step('Real: saved Play breadcrumb restore protects an unsaved duplicated draft, dismiss then accept', async () => {
    await page.getByRole('button', { name: 'Duplicate', exact: true }).click()
    await title().fill('E2E unsaved duplicate')
    await trail(['Overview', 'Plays', 'E2E P1 BTC long', 'E2E unsaved duplicate'])
    const before = await crumb().innerText(), url = page.url()
    let message
    page.once('dialog', async dialog => { message = dialog.message(); await dialog.dismiss() })
    await crumb().getByRole('link', { name: 'E2E P1 BTC long — previous location', exact: true }).click()
    assert.equal(message, 'Discard unsaved Play work and open this Play?')
    assert.equal(await crumb().innerText(), before)
    assert.equal(page.url(), url)
    assert.equal(await title().inputValue(), 'E2E unsaved duplicate')
    await shot('real-restore-cancel-keeps-draft')
    page.once('dialog', async dialog => { assert.equal(dialog.message(), message); await dialog.accept() })
    await crumb().getByRole('link', { name: 'E2E P1 BTC long — previous location', exact: true }).click()
    await until(async () => await title().inputValue() === 'E2E P1 BTC long', 'Saved Play restored after confirmation')
    await trail(['Overview', 'Plays', 'E2E P1 BTC long'])
  })
  await step('Real: account → linked saved Play collapses five crumbs; hidden locations expand and truncate; narrower densities', async () => {
    await nav('Overview')
    await page.getByRole('button', { name: `View ${ids.accountName}`, exact: true }).click()
    await page.getByRole('button', { name: 'E2E P1 BTC long, open Play', exact: true }).first().click()
    await title().waitFor()
    await trail(['Overview', '…', 'Plays', 'E2E P1 BTC long'])
    await crumb().getByRole('button', { name: 'Show 2 more locations', exact: true }).click()
    await shot('real-five-crumbs-expanded')
    await page.getByRole('link', { name: `${ids.accountName} — previous location`, exact: true }).click()
    await trail(['Overview', 'Accounts', ids.accountName])
    await page.getByRole('button', { name: 'E2E P1 BTC long, open Play', exact: true }).first().click()
    await page.setViewportSize({ width: 390, height: 844 })
    await trail(['…', 'Plays', 'E2E P1 BTC long'])
    await page.setViewportSize({ width: 320, height: 844 })
    await trail(['…', 'E2E P1 BTC long'])
    await shot('real-compact-crumbs')
    await page.setViewportSize({ width: 1440, height: 1000 })
  })
  for (const width of [1440, 1024, 390, 320]) await step(`Real Graphite layout at ${width}px; sticky on scroll, no overflow`, async () => {
    await nav('Overview')
    await layout(width, false, 'graphite')
    await page.evaluate(() => scrollTo(0, document.body.scrollHeight))
    assert.equal(await page.locator('.shell-header').evaluate(el => Math.round(el.getBoundingClientRect().top)), 0)
  })
  await page.setViewportSize({ width: 1440, height: 1000 })
  const paperReady = await step('Enable Paper through Settings UI; real paper-settings API and paperExecution venue available', async () => {
    await nav('Settings')
    await page.getByRole('switch', { name: 'Enable paper trading', exact: true }).check()
    const save = page.getByRole('button', { name: 'Save paper settings', exact: true })
    if (await save.isEnabled()) await save.click()
    await until(async () => (await api('/api/paper/settings')).enabled, 'Paper settings API reports enabled')
    await switchButton('real').waitFor()
    await shot('paper-enabled-settings')
  })
  if (paperReady) {
    await step('Brand lockup switches to Paper, focuses new control, announces mode and resets breadcrumb', async () => {
      await switchButton('real').click()
      await switchButton('paper').waitFor()
      await until(async () => await switchButton('paper').evaluate(el => el === document.activeElement), 'Focus moved to Paper mode control')
      assert.ok(await page.getByRole('status').filter({ hasText: /^Paper playground$/ }).count())
      await trail(['Settings'])
      await header(true)
      assert.equal(await page.locator('.journal-shell').getAttribute('data-trading-mode'), 'paper')
      assert.ok(await page.locator('.shell-sidebar').evaluate(el => getComputedStyle(el).boxShadow.includes('inset')))
      assert.ok(await page.locator('.journal-shell').evaluate(el => getComputedStyle(el).getPropertyValue('--paper-ink').trim()))
      assert.equal(await page.getByRole('combobox', { name: 'Trading mode', exact: true }).count(), 0)
      await shot('paper-lockup-arrival')
    })
    await step('Paper: create portfolio and Hyperliquid account through UI; default immutable virtual capital is 4000', async () => {
      await nav('Portfolios')
      await page.getByRole('button', { name: 'New portfolio', exact: true }).first().click()
      await page.getByLabel('Portfolio name', { exact: true }).fill(paperPortfolio)
      await page.getByRole('button', { name: 'Create portfolio', exact: true }).click()
      await page.getByRole('dialog').waitFor({ state: 'hidden' })
      await noItemPaper()
      await nav('Accounts')
      await page.getByRole('button', { name: 'Add account', exact: true }).first().click()
      await page.getByRole('radio', { name: 'Hyperliquid', exact: true }).check()
      await page.getByLabel('Paper portfolio · required', { exact: true }).selectOption({ label: paperPortfolio })
      await page.getByLabel('Account name', { exact: true }).fill(paperAccount)
      assert.equal(await page.getByLabel('Starting capital · virtual USD', { exact: true }).inputValue(), '4000')
      await page.getByRole('button', { name: 'Create paper account', exact: true }).click()
      await page.getByRole('dialog').waitFor({ state: 'hidden' })
      await page.getByRole('button', { name: `View ${paperAccount}`, exact: true }).waitFor()
      const accounts = await api('/api/accounts?tradingMode=paper')
      const account = accounts.find(a => a.name === paperAccount)
      assert.ok(account)
      assert.equal(account.startingCapitalUsd, '4000')
      assert.equal(account.tradingMode, 'paper')
      await noItemPaper()
      await shot('paper-account-created')
    })
    for (const name of ['Plays', 'Portfolios', 'Accounts', 'Activity']) await step(`Paper ${name}: one header disclosure; no redundant Paper chips`, async () => {
      await nav(name)
      await header(true)
      await noItemPaper()
      await shot(`paper-${name.toLowerCase()}`)
    })
    await step('Paper account detail: Enabled status, one disclosure and no redundant item chip', async () => {
      await nav('Accounts')
      await page.getByRole('button', { name: `View ${paperAccount}`, exact: true }).click()
      await header(true)
      await page.getByRole('region', { name: 'Account summary', exact: true }).getByText('Enabled', { exact: true }).waitFor()
      await shot('paper-account-detail')
      await noItemPaper()
    })
    await step('Paper: create and save a BTC Play through UI using fake venue catalogue', async () => {
      await nav('Plays')
      const all = page.getByRole('button', { name: 'All plays', exact: true })
      if (await all.isVisible()) await all.click()
      await page.getByRole('button', { name: 'New play', exact: true }).click()
      await title().fill('E2E Shell paper BTC')
      const picker = page.locator('.plays-account-field .account-picker-trigger')
      await picker.click()
      await step('Paper account picker: no redundant Paper chip', noItemPaper)
      await page.getByRole('option', { name: new RegExp(paperAccount) }).click()
      const instrument = page.getByRole('combobox', { name: 'Perpetual instrument', exact: true })
      await until(async () => await instrument.isEnabled(), 'Fake venue catalogue is selectable')
      await instrument.fill('BTC')
      await page.getByRole('option', { name: /BTC/ }).first().click()
      await page.getByRole('group', { name: 'Play actions', exact: true }).getByRole('button', { name: 'Save', exact: true }).click()
      await page.getByRole('group', { name: 'Play actions', exact: true }).getByRole('button', { name: 'Saved', exact: true }).waitFor()
      await header(true)
      await shot('paper-play-saved')
    })
    await step('Paper Play editor: one header disclosure and no redundant Paper chips', async () => {
      await header(true)
      await noItemPaper()
    })
    for (const width of [1024, 390, 320]) await step(`Paper Play editor Graphite at ${width}px: responsive controls, header and no overflow`, async () => {
      await layout(width, true, 'graphite-editor')
    })
    await step('Narrow unsaved-work dialog fits 320px; Escape keeps sheet/work and returns focus', async () => {
      await title().fill('E2E Shell paper BTC unsaved')
      await openSheet()
      await switchButton('paper').click()
      const dialog = page.getByRole('alertdialog', { name: 'Switch to Real journal?', exact: true })
      await dialog.waitFor()
      assert.equal(await dialog.getByRole('button', { name: 'Keep editing', exact: true }).evaluate(el => el === document.activeElement), true)
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth))
      await shot('paper-narrow-unsaved-switch-dialog')
      await page.keyboard.press('Escape')
      await dialog.waitFor({ state: 'hidden' })
      assert.ok(await switchButton('paper').isVisible())
      await until(async () => await switchButton('paper').evaluate(el => el === document.activeElement), 'Escape returns focus inside sheet')
      assert.equal(await title().inputValue(), 'E2E Shell paper BTC unsaved')
      await closeSheet()
    })
    await page.setViewportSize({ width: 1440, height: 1000 })
    await step('Paper unsaved work: Keep editing preserves work/route, returns focus; Discard switches, focuses, announces, resets trail', async () => {
      await title().fill('E2E Shell paper BTC unsaved')
      const before = await crumb().innerText(), url = page.url()
      await switchButton('paper').click()
      const dialog = page.getByRole('alertdialog', { name: 'Switch to Real journal?', exact: true })
      await dialog.waitFor()
      assert.equal(await dialog.getByRole('button', { name: 'Keep editing', exact: true }).evaluate(el => el === document.activeElement), true)
      await shot('paper-unsaved-switch-dialog')
      await dialog.getByRole('button', { name: 'Keep editing', exact: true }).click()
      await until(async () => await switchButton('paper').evaluate(el => el === document.activeElement), 'Keep editing returns focus')
      assert.equal(page.url(), url)
      assert.equal(await crumb().innerText(), before)
      assert.equal(await title().inputValue(), 'E2E Shell paper BTC unsaved')
      await switchButton('paper').click()
      await page.getByRole('alertdialog').getByRole('button', { name: 'Discard and switch', exact: true }).click()
      await switchButton('real').waitFor()
      await until(async () => await switchButton('real').evaluate(el => el === document.activeElement), 'Discard returns focus to new control')
      assert.ok(await page.getByRole('status').filter({ hasText: /^Real journal$/ }).count())
      await trail(['Plays'])
      await header()
      await shot('real-after-discard-switch')
    })
    await step('Collapsed desktop rail swap works both ways and preserves rail/focus', async () => {
      await nav('Overview')
      await page.getByRole('button', { name: 'Collapse navigation', exact: true }).click()
      assert.equal(await switchButton('real').getAttribute('class'), 'shell-mode-swap')
      await switchButton('real').click()
      await switchButton('paper').waitFor()
      assert.equal(await switchButton('paper').getAttribute('class'), 'shell-mode-swap')
      assert.ok(await page.getByRole('button', { name: 'Expand navigation', exact: true }).isVisible())
      await header(true)
      await shot('paper-collapsed-rail')
      await switchButton('paper').click()
      await switchButton('real').waitFor()
      await until(async () => await switchButton('real').evaluate(el => el === document.activeElement), 'Rail switch focus restored')
      await page.getByRole('button', { name: 'Expand navigation', exact: true }).click()
      await switchButton('real').click()
      await switchButton('paper').waitFor()
    })
    for (const theme of ['Graphite', 'Daylight']) {
      await step(`Paper: select ${theme} appearance through Settings UI`, async () => {
        await page.setViewportSize({ width: 1440, height: 1000 })
        await nav('Settings')
        await page.getByRole('button', { name: new RegExp(`^${theme}(\\s|$)`) }).click()
        await nav('Overview')
      })
      for (const width of [1440, 1024, 390, 320]) await step(`Paper ${theme} at ${width}px: header boat/sheet on narrow, one notice, no overflow`, async () => {
        await layout(width, true, theme.toLowerCase())
      })
    }
    await step('Narrow 320px sheet switch closes sheet, focuses menu and announces Real; switch back to Paper', async () => {
      await openSheet()
      await switchButton('paper').click()
      await until(async () => await page.getByRole('button', { name: 'Open navigation', exact: true }).evaluate(el => el === document.activeElement), 'Narrow switch focuses menu')
      await header()
      assert.ok(await page.getByRole('status').filter({ hasText: /^Real journal$/ }).count())
      await openSheet()
      await switchButton('real').click()
      await header(true)
    })
    await step('Return to Real Daylight for light-theme layout verification', async () => {
      await page.setViewportSize({ width: 1440, height: 1000 })
      await switchButton('paper').click()
      await switchButton('real').waitFor()
      await nav('Overview')
    })
    for (const width of [1440, 1024, 390, 320]) await step(`Real Daylight at ${width}px: sticky header, no Paper disclosure and no overflow`, async () => {
      await layout(width, false, 'daylight')
    })
  }
  await step('Zero browser console errors, page errors, CSP violations and external requests', async () => {
    assert.deepEqual(errors, [])
    assert.deepEqual(violations, [])
    assert.deepEqual(external, [])
  })
}
main().catch(error => {
  results.push({ label: 'Harness completion', result: 'FAIL', detail: clean(error.stack || error) })
}).finally(async () => {
  if (browser) await browser.close()
  const report = { seconds: (Date.now() - started) / 1000, results, errors, violations, external, screenshots, layouts }
  fs.mkdirSync(out, { recursive: true })
  fs.writeFileSync(path.join(out, 'shell-results.json'), JSON.stringify(report, null, 2))
  console.log(`Shell total: ${results.filter(r => r.result === 'PASS').length} passed, ${results.filter(r => r.result === 'FAIL').length} failed; ${report.seconds}s`)
  process.exitCode = results.some(r => r.result === 'FAIL') ? 1 : 0
})
