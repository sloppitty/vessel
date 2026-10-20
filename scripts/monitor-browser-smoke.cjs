// Deterministic UI checks against the production SPA. The release runner separately
// verifies the real worker, admission, event stream and broker recovery.
const assert = require('node:assert/strict')
const fs = require('node:fs/promises')
const path = require('node:path')

async function monitorFixture(page, accountId) {
  const now = new Date().toISOString()
  const issue = {
    id: '00000000-0000-4000-8000-0000000000d1', accountId, playId: null,
    contractId: 'BTC', kind: 'position.excess', severity: 'warning', state: 'open',
    openedAtUtc: now, confirmedAtUtc: now, resolvedAtUtc: null, dismissedAtUtc: null, note: null,
    details: { venueQuantity: '0.100000000000000001', playQuantity: '0.1' }, outcome: null, unverifiableAtUtc: null, lastObservedAtUtc: now,
  }
  // A publicly announced candidate whose subject stayed unobservable past the monitor lifetime.
  const unverifiable = {
    ...issue, id: '00000000-0000-4000-8000-0000000000d2', kind: 'position.missing', state: 'candidate', confirmedAtUtc: null,
    details: { venueQuantity: '0', playQuantity: '0.1' }, outcome: 'unverifiable', unverifiableAtUtc: now,
  }
  const monitor = {
    accountId, state: 'stale', lastAttemptAtUtc: now, lastSuccessAtUtc: now,
    nextRunAtUtc: null, manualState: 'idle', runTrigger: null, nextManualAllowedAtUtc: null, error: null,
    workerOnline: false, discrepancies: [issue, unverifiable],
  }
  let checks = 0, dismissals = 0, statusReads = 0, finishCheck, startCheck
  const checkStarted = new Promise(resolve => { startCheck = resolve })
  const json = (route, value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) })
  await page.route('**/api/monitor', route => json(route, {
    workerOnline: monitor.workerOnline, heartbeatAtUtc: now, accounts: [monitor],
  }))
  // Like the real API: the run is accepted as pending with the state unchanged. The worker then runs it,
  // and the browser sees it finish through stored status reads (or a stream event), not the 202 body.
  await page.route(`**/api/accounts/${accountId}/monitor`, route => {
    if (monitor.manualState !== 'idle' && ++statusReads > 1) {
      if (monitor.manualState === 'pending') monitor.manualState = 'running'
      else {
        const now = new Date()
        Object.assign(monitor, { manualState: 'idle', runTrigger: null, workerOnline: true, state: 'live', lastAttemptAtUtc: now.toISOString(),
          lastSuccessAtUtc: now.toISOString(), nextRunAtUtc: new Date(now.getTime() + 600_000).toISOString(),
          nextManualAllowedAtUtc: new Date(now.getTime() + 60_000).toISOString() })
      }
    }
    return json(route, monitor)
  })
  await page.route(`**/api/accounts/${accountId}/monitor/run`, async route => {
    assert.equal(route.request().method(), 'POST')
    checks++
    await new Promise(resolve => { finishCheck = resolve; startCheck() })
    monitor.manualState = 'pending'
    monitor.runTrigger = 'owner'
    await json(route, { requestId: '00000000-0000-4000-8000-0000000000c1', monitor }, 202)
  })
  await page.route(`**/api/accounts/${accountId}/discrepancies/${issue.id}/dismiss`, async route => {
    assert.equal(route.request().method(), 'POST')
    assert.equal(route.request().postDataJSON().note, 'Reviewed synthetic venue position')
    dismissals++
    issue.state = 'dismissed'
    issue.note = 'Reviewed synthetic venue position'
    issue.dismissedAtUtc = new Date().toISOString()
    await route.fulfill({ status: 204 })
  })
  return async pass => {
    const capture = async name => {
      if (!process.env.VESSEL_RELEASE_CAPTURE_DIR) return
      await fs.mkdir(process.env.VESSEL_RELEASE_CAPTURE_DIR, { recursive: true })
      await page.screenshot({ path: path.join(process.env.VESSEL_RELEASE_CAPTURE_DIR, name + '.png'), fullPage: true })
    }
    // Navigating and receiving events must not request venue checks.
    assert.equal(checks, 0)
    // Open incidents are visible above the Overview tiles and on the Plays entry, not only in a popup.
    await page.getByRole('link', { name: 'Overview', exact: true }).click()
    const incidents = page.getByRole('region', { name: 'Monitor incidents', exact: true })
    await incidents.getByText('2 incidents need attention', { exact: true }).waitFor()
    await incidents.getByRole('button', { name: /^The venue position is larger than expected · BTC/ }).waitFor()
    assert.ok(await incidents.evaluate(row => !!(row.compareDocumentPosition(document.querySelector('.overview-metrics')) & Node.DOCUMENT_POSITION_FOLLOWING)))
    await page.locator('#plays-nav-incidents').waitFor({ state: 'attached' })
    await capture('monitor-overview-incidents')
    await page.getByRole('link', { name: 'Settings', exact: true }).click()
    await page.getByRole('button', { name: /^Graphite(\s|$)/ }).click()
    await page.getByRole('link', { name: 'Accounts', exact: true }).click()
    await page.getByRole('button', { name: 'View Synthetic chart', exact: true }).click()
    const panel = page.getByRole('region', { name: 'Background monitor', exact: true })
    await panel.getByText('Worker offline.', { exact: false }).waitFor()
    await panel.getByText('Monitor stale', { exact: true }).waitFor()
    await panel.getByText('Needs attention · 2 warnings', { exact: true }).waitFor()
    const warnings = page.getByRole('region', { name: 'Monitor discrepancies', exact: true })
    const excess = warnings.getByRole('listitem').filter({ hasText: 'The venue position is larger than expected' })
    await excess.getByText('Recorded details', { exact: true }).click()
    await excess.getByText('0.100000000000000001', { exact: false }).waitFor()
    await warnings.getByText('warning · unverifiable', { exact: true }).waitFor()
    await warnings.getByText('It is not resolved and is no longer checked', { exact: false }).waitFor()
    await capture('monitor-desktop-dark')
    pass('stored stale/offline monitor, exact discrepancy details and an unverifiable outcome render without automatic Check')

    await panel.getByRole('button', { name: 'Check', exact: true }).click()
    await panel.getByRole('button', { name: 'Checking…', exact: true }).waitFor()
    assert.ok(await panel.getByRole('button').isDisabled())
    await checkStarted
    assert.equal(checks, 1)
    finishCheck()
    await panel.getByRole('button', { name: /^(Check pending…|Checking…)$/ }).waitFor()
    assert.ok(await panel.getByRole('button').isDisabled())
    // No stream event arrives for this synthetic run; the bounded status read must still end it.
    await panel.getByRole('button', { name: /^Check in \d+s$/ }).waitFor({ timeout: 20_000 })
    // The run is live again, but open records still head the panel: never "Monitored" beside them.
    await panel.getByText('Monitor stale', { exact: true }).waitFor({ state: 'detached' })
    await panel.getByText('Needs attention · 2 warnings', { exact: true }).waitFor()
    assert.equal(await panel.getByText('Monitored', { exact: true }).count(), 0)
    assert.ok(await panel.getByRole('button').isDisabled())
    assert.equal(checks, 1)
    pass('Check is explicit, stays pending until stored status completes, and is blocked during the server cooldown')

    await excess.getByRole('textbox', { name: 'Dismissal note: The venue position is larger than expected · BTC' }).fill('Reviewed synthetic venue position')
    await excess.getByRole('button', { name: 'Dismiss', exact: true }).click()
    await excess.getByText('warning · dismissed', { exact: true }).waitFor()
    assert.equal(dismissals, 1)
    assert.equal(await excess.getByRole('button', { name: 'Dismiss', exact: true }).count(), 0)
    // The unverifiable record stays announced, so the owner can still acknowledge it.
    assert.equal(await warnings.getByRole('listitem').filter({ hasText: 'The venue shows no position' }).getByRole('button', { name: 'Dismiss', exact: true }).count(), 1)
    await panel.getByText('Needs attention · 1 warning', { exact: true }).waitFor()
    await warnings.getByText('It does not change executions or stop checks', { exact: false }).waitFor()
    pass('dismissal persists its note and explains that monitoring continues')

    await page.getByRole('link', { name: 'Settings', exact: true }).click()
    await page.getByRole('button', { name: /^Paper(\s|$)/ }).click()
    await page.getByRole('link', { name: 'Accounts', exact: true }).click()
    await page.getByRole('button', { name: 'View Synthetic chart', exact: true }).click()
    await panel.waitFor()
    await capture('monitor-desktop-light')
    await page.setViewportSize({ width: 390, height: 844 })
    await panel.scrollIntoViewIfNeeded()
    assert.ok(await panel.isVisible())
    assert.ok(await panel.getByRole('button').isDisabled())
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth))
    await capture('monitor-mobile-light')
    assert.equal(checks, 1)
    pass('monitor controls remain visible at 390px in a light theme without horizontal page overflow')
  }
}

module.exports = monitorFixture
