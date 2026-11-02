#!/usr/bin/env node
// Run against verify-release.py's disposable HTTPS stack, never an installation.
// PLAYWRIGHT_MODULE and NOBLE_MODULE_DIR are supplied externally; no app dependencies.
const assert = require('node:assert/strict')
const { signer } = require('./wallet-test-signer.cjs')

async function main() {
  assert.equal(process.env.VESSEL_RELEASE_SYNTHETIC, '1')
  const base = process.env.VESSEL_RELEASE_URL
  assert.ok(base && new URL(base).protocol === 'https:')
  assert.ok(process.env.VESSEL_RELEASE_INVITATION)
  const engine = process.env.VESSEL_BROWSER || 'chromium'
  assert.ok(['chromium', 'firefox'].includes(engine))
  const playwright = require(process.env.PLAYWRIGHT_MODULE || 'playwright')
  const endpoint = process.env[`PLAYWRIGHT_${engine.toUpperCase()}_WS_ENDPOINT`]
  if (endpoint) {
    const url = new URL(endpoint)
    assert.ok(url.protocol === 'ws:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname) &&
      !url.username && !url.password, 'remote browser endpoint must be a loopback ws: URL')
  }
  const browser = endpoint ? await playwright[engine].connect(endpoint, { timeout: 60000 }) : await playwright[engine].launch({
    headless: true,
    ...(engine === 'chromium' && process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}),
  })
  assert.equal(browser.browserType().name(), engine)
  console.log(`Wallet ${engine}: ${browser.version()} (${endpoint ? 'remote' : 'local'})`)
  const contexts = [], errors = [], violations = []
  let checks = 0
  const pass = label => { checks++; console.log(`PASS [wallet-${engine}] ${label}`) }
  const first = engine === 'chromium' ? 2 : 4

  async function contextWithWallet(index, recovery) {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1512, height: 1120 } })
    contexts.push(context)
    const wallets = await Promise.all([index, ...recovery ? [recovery] : []].map(signer))
    let bindingChecked = false
    await context.exposeFunction('syntheticWalletRequest', async (slot, method, params) => {
      const wallet = wallets[slot]
      if (method === 'eth_requestAccounts') return [wallet.address]
      assert.equal(method, 'personal_sign')
      if (!bindingChecked) {
        hostCookie(await context.cookies(base), '__Host-vessel-signin')
        bindingChecked = true
      }
      assert.equal(params[1].toLowerCase(), wallet.address)
      return wallet.sign(params[0].startsWith('0x') ? Buffer.from(params[0].slice(2), 'hex').toString('utf8') : params[0])
    })
    await context.exposeFunction('recordWalletCsp', entry => violations.push(entry))
    await context.addInitScript(count => {
      document.addEventListener('securitypolicyviolation', event => window.recordWalletCsp({
        directive: event.violatedDirective, blocked: event.blockedURI,
      }))
      const announcements = Array.from({ length: count }, (_, slot) => ({
        info: {
          uuid: `00000000-0000-4000-8000-00000000000${slot}`,
          name: slot ? 'Synthetic recovery' : 'Synthetic wallet',
          icon: 'data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg"/>',
          rdns: `test.vessel.synthetic.${slot}`,
        },
        provider: {
          request: ({ method, params }) => window.syntheticWalletRequest(slot, method, params),
          on() {}, removeListener() {},
        },
      }))
      const announce = () => announcements.forEach(detail => window.dispatchEvent(new CustomEvent('eip6963:announceProvider', { detail })))
      window.addEventListener('eip6963:requestProvider', announce)
      announce()
    }, wallets.length)
    await context.route(url => url.origin !== new URL(base).origin, route => {
      errors.push('Unexpected external browser connection: ' + new URL(route.request().url()).origin)
      return route.abort()
    })
    context.on('page', page => page.on('pageerror', error => errors.push(error.message)))
    return context
  }

  function hostCookie(cookies, name) {
    const cookie = cookies.find(item => item.name === name)
    assert.ok(cookie && cookie.secure && cookie.httpOnly && cookie.sameSite === 'Strict' &&
      cookie.path === '/' && cookie.domain === new URL(base).hostname, `${name} must be a secure host-only cookie`)
  }

  async function issuedCookie(response, name) {
    const values = (await response.headerValues('set-cookie')).filter(value => value.startsWith(name + '='))
    assert.equal(values.length, 1)
    for (const attribute of ['Secure', 'HttpOnly', 'SameSite=Strict', 'Path=/']) {
      assert.ok(values[0].split(';').some(part => part.trim().toLowerCase() === attribute.toLowerCase()))
    }
    assert.ok(!/;\s*domain\s*=/i.test(values[0]), `${name} must omit Domain`)
  }

  async function issuedCookieIn(context, name) {
    hostCookie(await context.cookies(base), name)
  }

  async function signIn(page, name) {
    await page.goto(base)
    await page.getByRole('button', { name: /Synthetic wallet/ }).click()
    const challenge = page.waitForResponse(response => response.url().endsWith('/api/auth/challenge') &&
      response.request().method() === 'POST')
    const issued = page.waitForResponse(response => response.url().endsWith(name ? '/api/auth/register' : '/api/auth/verify') &&
      response.request().method() === 'POST')
    await page.getByRole('button', { name: 'Sign with Synthetic wallet', exact: true }).click()
    await issuedCookie(await challenge, '__Host-vessel-signin')
    if (name) {
      await page.getByRole('heading', { name: 'Create your journal', exact: true }).waitFor()
      await page.getByLabel('Display name', { exact: true }).fill(name)
      await page.getByLabel(/Invitation code/).fill(process.env.VESSEL_RELEASE_INVITATION)
      await page.getByRole('button', { name: 'Create journal', exact: true }).click()
    }
    await page.getByRole('region', { name: 'Overview summary', exact: true }).waitFor()
    await issuedCookie(await issued, '__Host-vessel-session')
    const session = await call(page, '/api/auth/session')
    assert.equal(session.status, 200)
    return session.body
  }

  // These requests execute in a real browser, including its Origin and Fetch Metadata.
  async function call(page, path, method = 'GET', body, user, extra = {}) {
    return page.evaluate(async ({ path, method, body, user, extra }) => {
      const response = await fetch(path, {
        method, credentials: 'same-origin', referrerPolicy: 'same-origin',
        headers: {
          ...extra,
          ...(user ? { 'X-Vessel-User': user } : {}),
          ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        },
        ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
      })
      const text = await response.text()
      return { status: response.status, body: text ? JSON.parse(text) : null }
    }, { path, method, body, user, extra })
  }

  try {
    const a = await contextWithWallet(first, first + 1)
    const pageA = await a.newPage()
    const sessionA = await signIn(pageA, `Synthetic ${engine}`)
    const ownerA = sessionA.user.id
    pass('EIP-6963 chooser → external EOA signature → invitation → journal → shell')
    hostCookie(await a.cookies(base), '__Host-vessel-session')
    pass('HTTPS binding/session cookies are __Host-, Secure, HttpOnly, SameSite=Strict, Path=/ with no Domain')

    const portfolio = await call(pageA, '/api/portfolios', 'POST', { name: `Private ${engine}`, icon: 'folder' }, ownerA)
    assert.equal(portfolio.status, 201)
    const disposable = await call(pageA, '/api/portfolios', 'POST', { name: 'Delete me', icon: 'folder' }, ownerA)
    assert.equal(disposable.status, 201)
    assert.equal((await call(pageA, '/api/portfolios/' + disposable.body.id, 'DELETE', undefined, ownerA)).status, 204)
    pass('browser POST and bodyless DELETE pass the Wallet request guard')

    const account = await call(pageA, '/api/accounts', 'POST',
      { name: 'Private manual', venueId: 'manual', portfolioId: portfolio.body.id, manualAccountValueUsd: '1000' }, ownerA)
    assert.equal(account.status, 201)
    const plan = {
      direction: 'long', sizingMode: 'margin', size: '100', leverage: '1',
      entries: [{ id: 'entry-1', name: 'Entry 1', color: '#b9c9e4', share: '100', price: '100',
        stops: [{ id: 'stop-1', unit: 'price', value: '95', share: '100' }],
        targets: [{ id: 'target-1', unit: 'price', value: '110', share: '100' }] }],
      notes: { thesis: 'Synthetic Wallet smoke', invalidation: '', strategy: '', evidence: '' },
    }
    const play = await call(pageA, '/api/plays', 'POST',
      { accountId: account.body.id, instrument: 'SYNTH', instrumentSource: 'manual', title: 'Private evidence', plan }, ownerA)
    assert.equal(play.status, 201)
    const evidence = await pageA.evaluate(async ({ playId, user }) => {
      const data = new FormData()
      const image = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='), c => c.charCodeAt(0))
      data.append('file', new Blob([image], { type: 'image/png' }), 'synthetic.png')
      const response = await fetch(`/api/plays/${playId}/evidence`, {
        method: 'POST', body: data, headers: { 'X-Vessel-User': user },
        credentials: 'same-origin', referrerPolicy: 'same-origin',
      })
      return { status: response.status, body: await response.json() }
    }, { playId: play.body.summary.id, user: ownerA })
    assert.equal(evidence.status, 201)
    pass('browser multipart evidence upload passes the Wallet guard')

    await pageA.getByRole('button', { name: /account menu$/ }).click()
    await pageA.getByRole('menuitem', { name: 'Profile & sign-in', exact: true }).click()
    await pageA.getByLabel('Display name', { exact: true }).fill(`Updated ${engine}`)
    const patch = pageA.waitForResponse(response => response.url().endsWith('/api/auth/profile') && response.request().method() === 'PATCH')
    await pageA.getByRole('button', { name: 'Save', exact: true }).click()
    assert.equal((await patch).status(), 200)
    pass('frontend profile PATCH passes the Wallet guard')

    await pageA.getByRole('button', { name: 'Link wallet', exact: true }).click()
    const dialog = pageA.getByRole('dialog', { name: 'Link another wallet', exact: true })
    await dialog.getByRole('button', { name: /Synthetic wallet/ }).click()
    await dialog.getByRole('button', { name: 'Sign with Synthetic wallet', exact: true }).click()
    await dialog.getByRole('button', { name: /Synthetic recovery/ }).click()
    await dialog.getByRole('button', { name: 'Sign with Synthetic recovery', exact: true }).click()
    await dialog.waitFor({ state: 'hidden' })
    const wallets = await call(pageA, '/api/auth/wallets', 'GET', undefined, ownerA)
    assert.equal(wallets.status, 200)
    assert.equal(wallets.body.length, 2)
    pass('second wallet links through linked-wallet step-up and its own signature')

    const b = await contextWithWallet(1)
    const pageB = await b.newPage()
    const sessionB = await signIn(pageB)
    assert.notEqual(sessionB.user.id, ownerA)
    const listA = await call(pageA, '/api/portfolios', 'GET', undefined, ownerA)
    const listB = await call(pageB, '/api/portfolios', 'GET', undefined, sessionB.user.id)
    assert.equal(listA.status, 200)
    assert.equal(listB.status, 200)
    assert.ok(listA.body.some(item => item.id === portfolio.body.id))
    assert.ok(listB.body.some(item => item.name === 'Release synthetic'), 'operator sees its seeded portfolio')
    assert.ok(!listB.body.some(item => item.id === portfolio.body.id))
    assert.equal((await call(pageB, '/api/accounts/' + account.body.id, 'GET', undefined, sessionB.user.id)).status, 404)
    assert.equal((await call(pageB, '/api/evidence/' + evidence.body.id + '/content', 'GET', undefined, sessionB.user.id)).status, 404)
    assert.ok(!listA.body.some(item => listB.body.some(other => other.id === item.id)))
    pass('two user contexts have disjoint journals; foreign account/evidence return 404')

    // Both sessions are live. A newly registered owner (not the bootstrap
    // operator) must be discovered by the Hub without re-provisioning.
    const beforeB = await call(pageB, '/api/notifications?tab=all', 'GET', undefined, sessionB.user.id)
    assert.equal(beforeB.status, 200)
    const bIds = beforeB.body.items.map(item => item.id)
    // The operator already received the packaged API test events. Settle its
    // own backlog before checking that A cannot cause a popup in B's context.
    for (const item of beforeB.body.items) {
      assert.equal((await call(pageB, '/api/notifications/' + item.id + '/read',
        'POST', undefined, sessionB.user.id)).status, 200)
    }
    await pageB.reload()
    await pageB.getByRole('region', { name: 'Overview summary', exact: true }).waitFor()
    assert.equal(await pageB.getByText('Test notification', { exact: true }).count(), 0)
    assert.equal((await call(pageA, '/api/notifications/test', 'POST', undefined, ownerA)).status, 202)
    let notification
    for (let attempt = 0; attempt < 60; attempt++) {
      const feed = await call(pageA, '/api/notifications?tab=all', 'GET', undefined, ownerA)
      assert.equal(feed.status, 200)
      notification = feed.body.items.find(item => item.kind === 'test')
      if (notification) break
      await pageA.waitForTimeout(500)
    }
    assert.ok(notification, 'new Wallet owner must receive its test event through the Hub')
    await pageA.getByText('Test notification', { exact: true }).waitFor()
    const afterB = await call(pageB, '/api/notifications?tab=all', 'GET', undefined, sessionB.user.id)
    assert.equal(afterB.status, 200)
    assert.equal(afterB.body.allTotal, beforeB.body.allTotal)
    assert.deepEqual(afterB.body.items.map(item => item.id), bIds)
    assert.equal(await pageB.getByText('Test notification', { exact: true }).count(), 0)
    assert.equal((await call(pageB, '/api/notifications/' + notification.id + '/read',
      'POST', undefined, sessionB.user.id)).status, 404)
    pass('Wallet test notification appears only for its owner; the other session cannot list, show or read it')

    // A separate same-origin document has no application navigation or stream
    // teardown: only the server can end this reader after session revocation.
    const streamPage = await a.newPage()
    await streamPage.goto(base + '/health/live')
    await streamPage.evaluate(user => {
      window.streamResult = { ready: false, ended: false }
      void (async () => {
        const response = await fetch('/api/events/stream', { headers: { 'X-Vessel-User': user } })
        window.streamResult.status = response.status
        const reader = response.body.getReader()
        await reader.read()
        window.streamResult.ready = true
        try { while (!(await reader.read()).done) {} }
        finally { window.streamResult.ended = true; window.streamResult.endedAt = Date.now() }
      })()
    }, ownerA)
    await streamPage.waitForFunction(() => window.streamResult.ready)
    assert.equal(await streamPage.evaluate(() => window.streamResult.status), 200)

    const revoker = await contextWithWallet(first + 1)
    const revokerPage = await revoker.newPage()
    const revokerSession = await signIn(revokerPage)
    assert.equal(revokerSession.user.id, ownerA)
    await revokerPage.getByRole('button', { name: /account menu$/ }).click()
    await revokerPage.getByRole('menuitem', { name: 'Profile & sign-in', exact: true }).click()
    const revoke = revokerPage.waitForResponse(response =>
      response.url().endsWith('/api/auth/sessions/' + sessionA.session.id) && response.request().method() === 'DELETE')
    assert.equal(await streamPage.evaluate(() => window.streamResult.ended), false, 'stream must still be open before revocation')
    const started = Date.now()
    await revokerPage.getByRole('button', { name: /^Sign out .* session$/ }).click()
    assert.equal((await revoke).status(), 204)
    await streamPage.waitForFunction(() => window.streamResult.ended, null, { timeout: 20000 })
    const elapsed = await streamPage.evaluate(() => window.streamResult.endedAt) - started
    assert.ok(elapsed >= 0 && elapsed <= 20000, `stream revocation took ${elapsed} ms`)
    pass(`frontend bodyless DELETE revokes a session; server closes its open stream in ${elapsed} ms (15 s + 5 s margin)`)
    assert.equal((await call(streamPage, '/api/auth/session')).status, 401)
    await revokerPage.getByRole('button', { name: /account menu$/ }).click()
    await revokerPage.getByRole('menuitem', { name: 'Sign out', exact: true }).click()
    await revokerPage.getByRole('heading', { name: 'Sign in to Vessel', exact: true }).waitFor()
    assert.equal((await call(revokerPage, '/api/auth/session')).status, 401)
    pass('voluntary frontend sign-out revokes its session and returns to the chooser')
    // ---- Device pairing: a phone with no wallet signs in to the same journal ----
    // The packaged stack is served on localhost, which a phone cannot open, so the desktop's Connect a phone dialog
    // explains instead of drawing a QR (checked below). The pairing itself is created and approved through the very
    // session API the dialog uses, after a real device.pair step-up signature; the phone side is the real screen.
    const pairer = await contextWithWallet(first + 1)
    const pagePair = await pairer.newPage()
    const pairSession = await signIn(pagePair)
    assert.equal(pairSession.user.id, ownerA)
    const pairWallet = await signer(first + 1)
    await pagePair.getByRole('button', { name: /account menu$/ }).click()
    await pagePair.getByRole('menuitem', { name: 'Profile & sign-in', exact: true }).click()
    await pagePair.getByRole('button', { name: 'Connect a phone', exact: true }).click()
    const connect = pagePair.getByRole('dialog', { name: 'Connect a phone', exact: true })
    await connect.getByRole('note').waitFor()
    assert.match(await connect.getByRole('note').innerText(), /A phone can.t open this address/)
    assert.equal(await connect.getByRole('img').count(), 0)
    await connect.getByRole('button', { name: 'Close', exact: true }).first().click()
    pass('desktop Connect a phone explains a loopback address and draws no QR')

    // The public auth routes insist on this header, as the frontend's public client sends it.
    const publicRequest = { 'X-Vessel-Request': '1' }

    async function stepUpPair() {
      const challenge = await call(pagePair, '/api/auth/step-up/challenge', 'POST', { action: 'device.pair', address: pairWallet.address }, ownerA)
      assert.equal(challenge.status, 200)
      const done = await call(pagePair, '/api/auth/step-up', 'POST',
        { challengeId: challenge.body.challengeId, signature: pairWallet.sign(challenge.body.message) }, ownerA)
      assert.equal(done.status, 204)
      const created = await call(pagePair, '/api/auth/pairings', 'POST', undefined, ownerA)
      assert.equal(created.status, 201)
      assert.equal(created.body.secret.length, 43)
      assert.match(created.body.typedCode, /^[0-9A-Z]{5}-[0-9A-Z]{5}$/)
      return created.body
    }

    async function phoneContext() {
      // A phone: narrow touch viewport, no wallet and no camera permission. `isMobile` is not available in Firefox.
      const context = await browser.newContext({
        ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 }, hasTouch: true, ...engine === 'chromium' ? { isMobile: true } : {},
      })
      contexts.push(context)
      await context.exposeFunction('recordWalletCsp', entry => violations.push(entry))
      await context.addInitScript(() => document.addEventListener('securitypolicyviolation', event => window.recordWalletCsp({
        directive: event.violatedDirective, blocked: event.blockedURI,
      })))
      await context.route(url => url.origin !== new URL(base).origin, route => {
        errors.push('Unexpected external browser connection: ' + new URL(route.request().url()).origin)
        return route.abort()
      })
      context.on('page', page => page.on('pageerror', error => errors.push(error.message)))
      return context
    }

    async function confirmationCode(page) {
      await page.getByRole('heading', { name: 'Confirm on your computer', exact: true }).waitFor()
      const text = await page.locator('.pairing-digits').textContent()
      const match = /(\d{3}) (\d{3})/.exec(text)
      assert.ok(match, 'the phone shows a grouped six-digit confirmation code')
      return match[1] + match[2]
    }

    // 1. The link the QR carries: secret in the fragment, removed from the address at once.
    const link = await stepUpPair()
    const phoneOne = await phoneContext()
    const pagePhone = await phoneOne.newPage()
    const landing = await pagePhone.goto(`${base}/#pair=${link.secret}`)
    assert.equal(landing.headers()['permissions-policy'], 'camera=(self), microphone=(), geolocation=()')
    const codeOne = await confirmationCode(pagePhone)
    assert.ok(!pagePhone.url().includes('pair='), 'the secret is removed from the address')
    assert.ok(await pagePhone.getByRole('checkbox', { name: 'Keep me signed in on this device', exact: true }).isChecked(), 'keep me signed in is checked by default on the phone')
    const claimed = await call(pagePair, '/api/auth/pairings/' + link.id, 'GET', undefined, ownerA)
    assert.equal(claimed.status, 200)
    assert.equal(claimed.body.status, 'claimed')
    assert.ok(claimed.body.device)
    const wrong = await call(pagePair, `/api/auth/pairings/${link.id}/approve`, 'POST', { code: codeOne === '000000' ? '111111' : '000000' }, ownerA)
    assert.equal(wrong.status, 400)
    assert.equal(wrong.body.code, 'pairing-code-invalid')
    assert.equal(wrong.body.attemptsLeft, 2)
    assert.equal((await call(pagePair, `/api/auth/pairings/${link.id}/approve`, 'POST', { code: codeOne }, ownerA)).status, 204)
    await pagePhone.getByRole('region', { name: 'Overview summary', exact: true }).waitFor({ timeout: 20000 })
    await issuedCookieIn(phoneOne, '__Host-vessel-session')
    const phoneSession = await call(pagePhone, '/api/auth/session')
    assert.equal(phoneSession.status, 200)
    assert.equal(phoneSession.body.user.id, ownerA)
    assert.equal(phoneSession.body.session.remember, true)
    const rows = await call(pagePair, '/api/auth/sessions', 'GET', undefined, ownerA)
    assert.equal(rows.body.find(row => row.id === phoneSession.body.session.id)?.source, 'pairing')
    await pagePair.reload()
    await pagePair.getByRole('button', { name: /account menu$/ }).click()
    await pagePair.getByRole('menuitem', { name: 'Profile & sign-in', exact: true }).click()
    await pagePair.getByText(/^Paired · /).first().waitFor()
    pass('QR link: phone claims the fragment secret, the desktop approves its code (wrong code counted), the phone lands signed in and Sessions says Paired')
    assert.equal((await call(pagePair, '/api/auth/pairings/' + link.id, 'GET', undefined, ownerA)).body.status, 'completed')
    assert.equal((await call(pagePhone, '/api/auth/pairings/complete', 'POST', { remember: true }, undefined, publicRequest)).status, 400, 'the used pairing cannot complete twice')

    // 2. The typed code, from the sign-in screen, with Keep me signed in turned off.
    const typed = await stepUpPair()
    const phoneTwo = await phoneContext()
    const pagePhoneTwo = await phoneTwo.newPage()
    await pagePhoneTwo.goto(base)
    await pagePhoneTwo.getByRole('heading', { name: 'Sign in to Vessel', exact: true }).waitFor()
    // A browser that can use a camera offers the scanner first; otherwise the typed code is offered directly.
    const scan = pagePhoneTwo.getByRole('button', { name: 'Scan a code from your desktop', exact: true })
    if (await scan.count()) await scan.click()
    await pagePhoneTwo.getByRole('button', { name: 'Enter a code instead', exact: true }).click()
    await pagePhoneTwo.getByLabel('Code from your computer', { exact: true }).fill(typed.typedCode.toLowerCase().replace('-', ' '))
    await pagePhoneTwo.getByRole('button', { name: 'Continue', exact: true }).click()
    const codeTwo = await confirmationCode(pagePhoneTwo)
    await pagePhoneTwo.getByRole('checkbox', { name: 'Keep me signed in on this device', exact: true }).uncheck()
    assert.equal((await call(pagePair, `/api/auth/pairings/${typed.id}/approve`, 'POST', { code: codeTwo }, ownerA)).status, 204)
    await pagePhoneTwo.getByRole('region', { name: 'Overview summary', exact: true }).waitFor({ timeout: 20000 })
    const typedSession = await call(pagePhoneTwo, '/api/auth/session')
    assert.equal(typedSession.body.user.id, ownerA)
    assert.equal(typedSession.body.session.remember, false)
    pass('typed code: phone enters a lower-case spaced code, the desktop approves, and the unchecked Keep me signed in is honoured')

    // 3. A second device claiming the same secret contests it: neither phone gets in.
    const shared = await stepUpPair()
    const phoneThree = await phoneContext()
    const pageThree = await phoneThree.newPage()
    await pageThree.goto(`${base}/#pair=${shared.secret}`)
    await confirmationCode(pageThree)
    const phoneFour = await phoneContext()
    const pageFour = await phoneFour.newPage()
    await pageFour.goto(`${base}/#pair=${shared.secret}`)
    await pageFour.getByText('Another device used this code', { exact: true }).waitFor()
    await pageThree.getByText('Another device used this code', { exact: true }).waitFor({ timeout: 20000 })
    pass('a second claim contests the pairing and both phones say so')
    const stranger = await phoneContext()
    const pageStranger = await stranger.newPage()
    await pageStranger.goto(base)
    assert.equal((await call(pageStranger, '/api/auth/pairings/complete', 'POST', { remember: true }, undefined, publicRequest)).status, 400, 'no binding cookie, no completion')
    // Revoking from the desktop signs the paired phones out; the release restore check expects no live session but the operator's.
    assert.equal((await call(pagePair, '/api/auth/sessions/revoke-others', 'POST', undefined, ownerA)).status, 204)
    assert.equal((await call(pagePhone, '/api/auth/session')).status, 401)
    assert.equal((await call(pagePhoneTwo, '/api/auth/session')).status, 401)
    assert.equal((await call(pagePair, '/api/auth/sign-out', 'POST', { expectedSessionId: pairSession.session.id }, ownerA)).status, 204)
    pass('revoking other sessions on the desktop signs both paired phones out')

    assert.deepEqual(errors, [])
    assert.deepEqual(violations, [])
    pass('zero page errors, CSP violations or outbound venue requests')
    console.log(`Wallet ${engine} checks: ${checks}`)
  } finally {
    await Promise.all(contexts.map(context => context.close()))
    await browser.close()
  }
}

main().catch(error => { console.error(error); process.exitCode = 1 })
