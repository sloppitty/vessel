// Production UI + real API smoke check. Run only against an explicitly synthetic, isolated
// database. Playwright/axe may be supplied externally; neither is a production dependency.
// It signs in with a publicly known synthetic EOA (scripts/wallet-test-signer.cjs, index VESSEL_REVIEW_SMOKE_WALLET,
// default 1) that the isolated installation bound with `identity bind-wallet`; set VESSEL_RELEASE_SYNTHETIC=1 and an
// external NOBLE_MODULE_DIR for the signer.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright')
const assert = require('node:assert/strict')
const { signer, exposeSyntheticWallet, signInWithSyntheticWallet } = require('./wallet-test-signer.cjs')
const { mkdir, writeFile } = require('node:fs/promises')
const path = require('node:path')

async function main() {
  assert.equal(process.env.VESSEL_REVIEW_SMOKE_SYNTHETIC, '1', 'Use an isolated synthetic database, not personal history.')
  const wallet = await signer(Number(process.env.VESSEL_REVIEW_SMOKE_WALLET || '1'))
  const browser = await chromium.launch({
    headless: true, ...(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}),
  })
  const out = path.resolve(__dirname, '../docs/design/portfolio-review/implementation')
  await mkdir(out, { recursive: true })
  try {
    const page = await browser.newPage({ viewport: { width: 1512, height: 1120 }, reducedMotion: 'reduce' })
    await exposeSyntheticWallet(page, wallet)
    const resize = async (width, height = 1000) => {
      await page.setViewportSize({ width, height })
      // Chromium can acknowledge the resize before media-query styles have been painted.
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    }
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    await signInWithSyntheticWallet(page, process.env.REVIEW_PREVIEW_URL || 'http://127.0.0.1:5193')
    await page.getByRole('link', { name: 'Review', exact: true }).click()
    const ready = () => page.getByRole('region', { name: 'Performance scorecard' }).waitFor()
    await ready()
    await page.evaluate(() => document.fonts.ready)
    const shot = async name => {
      await page.mouse.move(0, 0)
      await page.evaluate(() => window.scrollTo(0, 0))
      await page.screenshot({ path: path.join(out, name), fullPage: true })
    }
    await shot('desktop-overview.png')
    for (const name of ['Last 24 hours', 'Last 7 days', 'Last 30 days', 'Last 365 days', 'All recorded']) {
      await page.getByRole('button', { name, exact: true }).click()
      await ready()
      const button = page.getByRole('button', { name, exact: true })
      assert.equal(await button.getAttribute('aria-pressed'), 'true')
      assert.equal(await page.locator('.pr-breakdown-table tfoot td').nth(4).innerText(), await button.locator('strong').innerText())
    }
    await page.locator('.pr-section-label button').click()
    const dialog = page.getByRole('dialog')
    await dialog.locator('tbody tr').first().waitFor()
    assert.equal(await dialog.locator('tbody tr').count(), 50)
    const first = await dialog.locator('tbody tr').first().innerText()
    await dialog.getByRole('button', { name: 'Next', exact: true }).click()
    await dialog.getByText('Loading Plays…').waitFor({ state: 'hidden' })
    assert.equal(await dialog.locator('tbody tr').count(), 50)
    assert.notEqual(await dialog.locator('tbody tr').first().innerText(), first)
    await page.screenshot({ path: path.join(out, 'closed-plays.png') })
    await page.keyboard.press('Escape')
    await dialog.waitFor({ state: 'hidden' })
    assert.equal(await page.locator('.pr-section-label button').evaluate(el => el === document.activeElement), true)

    await page.getByRole('button', { name: 'Last 30 days', exact: true }).click()
    await ready()
    await page.getByRole('button', { name: 'Assets', exact: true }).click()
    await page.getByRole('button', { name: 'Daily', exact: true }).click()
    await shot('desktop-assets.png')
    await page.getByRole('button', { name: 'Drawdown', exact: true }).click()
    assert.equal(await page.locator('.pr-chart h2').innerText(), 'Daily-closing drawdown')
    await page.getByText('Chart data', { exact: true }).click()
    assert.ok(await page.getByRole('table', { name: /Chart values/ }).isVisible())

    const account = page.getByRole('combobox', { name: 'Account', exact: true })
    const portfolio = page.getByRole('combobox', { name: 'Portfolio', exact: true })
    await account.selectOption({ label: 'Core perps' })
    await ready()
    await portfolio.selectOption('unassigned')
    await ready()
    assert.equal(await account.inputValue(), '')
    assert.equal(await account.locator('option').count(), 2)
    await page.getByRole('button', { name: 'Reset', exact: true }).click()
    await ready()
    await page.getByRole('button', { name: 'Accounts', exact: true }).click()
    await portfolio.selectOption({ label: 'Swing trading' })
    await ready()
    await page.getByRole('button', { name: 'P&L', exact: true }).click()
    await shot('portfolio-accounts.png')
    await page.getByRole('button', { name: 'Reset', exact: true }).click()
    await ready()

    // Endpoint failure must replace the scorecard, not leave stale performance or demo results.
    await page.route('**/api/review?**', route => route.fulfill({ status: 503, contentType: 'application/problem+json', body: '{}' }))
    await page.getByRole('button', { name: 'Reload review' }).click()
    await page.getByRole('button', { name: 'Retry review' }).waitFor()
    assert.equal(await page.getByRole('region', { name: 'Performance scorecard' }).count(), 0)
    await page.unroute('**/api/review?**')
    await page.getByRole('button', { name: 'Retry review' }).click()
    await ready()

    const widths = [320, 390, 540, 768, 1024, 1280, 1512]
    for (const width of widths) {
      await resize(width)
      const geometry = await page.evaluate(() => {
        const rect = el => { const r = el.getBoundingClientRect(); return { width: r.width, height: r.height, right: r.right, center: r.top + r.height / 2 } }
        return { viewport: innerWidth, document: document.documentElement.scrollWidth,
          filters: [...document.querySelectorAll('.pr-select')].map(el => ({ select: rect(el.querySelector('select')), arrow: rect(el.querySelector('svg')) })),
          reset: rect(document.querySelector('.pr-reset')) }
      })
      assert.ok(geometry.document <= width, `Overflow at ${width}: ${JSON.stringify(geometry)}`)
      assert.ok(geometry.filters.every(({ select, arrow }) => Math.abs(select.center - arrow.center) < .5), `Selector arrow alignment at ${width}`)
      assert.ok(geometry.filters.every(({ select }) => select.height === geometry.reset.height), `Reset height at ${width}`)
      if (width > 800) assert.ok(Math.abs(geometry.filters[0].select.center - geometry.reset.center) < .5, `Reset alignment at ${width}`)
      await page.getByRole('button', { name: 'Definitions' }).click()
      assert.ok(await dialog.evaluate(el => { const r = el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight }), `Dialog bounds at ${width}`)
      await page.keyboard.press('Escape')
      await dialog.waitFor({ state: 'hidden' })
    }
    const accessibility = []
    if (process.env.AXE_MODULE) {
      for (const width of [390, 1512]) {
        await resize(width, 1120)
        await page.addScriptTag({ path: require.resolve(process.env.AXE_MODULE) })
        const violations = await page.evaluate(async () => (await window.axe.run()).violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.map(n => n.target) })))
        accessibility.push({ width, violations })
        assert.deepEqual(violations, [], `Accessibility at ${width}`)
      }
    }
    await resize(390)
    await shot('mobile-overview.png')
    assert.deepEqual(errors, [])
    const result = { passed: true, widths, errors, accessibility, fixture: '525 synthetic saved closed Plays in isolated PostgreSQL; production API/UI, no demo fallback', captures: out }
    await writeFile(path.join(out, 'verification.json'), JSON.stringify(result, null, 2) + '\n')
    console.log(JSON.stringify(result, null, 2))
  } finally { await browser.close() }
}
main().catch(error => { console.error(error); process.exitCode = 1 })
