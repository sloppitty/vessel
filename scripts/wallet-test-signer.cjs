// Disposable checks only. These publicly known keys must NEVER hold funds.
// Dependencies live outside the application checkout (NOBLE_MODULE_DIR).
const assert = require('node:assert/strict')
const path = require('node:path')
const { pathToFileURL } = require('node:url')

async function signer(index) {
  assert.equal(process.env.VESSEL_RELEASE_SYNTHETIC, '1', 'Synthetic services only')
  assert.ok(Number.isInteger(index) && index >= 1 && index <= 20)
  const root = process.env.NOBLE_MODULE_DIR
  assert.ok(root && path.isAbsolute(root), 'NOBLE_MODULE_DIR must be an absolute external node_modules directory')
  const { secp256k1 } = await import(pathToFileURL(path.join(root, '@noble/curves/secp256k1.js')))
  const { keccak_256 } = await import(pathToFileURL(path.join(root, '@noble/hashes/sha3.js')))
  const key = Uint8Array.from(Buffer.from(index.toString(16).padStart(64, '0'), 'hex'))
  const publicKey = secp256k1.getPublicKey(key, false)
  const address = '0x' + Buffer.from(keccak_256(publicKey.slice(1))).subarray(-20).toString('hex')
  return {
    address,
    sign(message) {
      assert.equal(typeof message, 'string')
      const bytes = Buffer.from(message, 'utf8')
      const prefix = Buffer.from(`\x19Ethereum Signed Message:\n${bytes.length}`, 'utf8')
      const digest = keccak_256(Buffer.concat([prefix, bytes]))
      // Noble v2 recovered format is recovery byte || r || s; Ethereum uses r || s || (27 + recovery).
      const signature = secp256k1.sign(digest, key, { prehash: false, format: 'recovered' })
      return '0x' + Buffer.concat([Buffer.from(signature.slice(1)), Buffer.from([27 + signature[0]])]).toString('hex')
    },
  }
}

/**
 * Announces one synthetic EIP-6963 wallet in every page of `target` (a Playwright browser context or page). Only
 * `eth_requestAccounts` and `personal_sign` are answered, by `wallet` from `signer()`.
 */
async function exposeSyntheticWallet(target, wallet, name = 'Synthetic wallet') {
  await target.exposeFunction('syntheticWalletRequest', async (method, params) => {
    if (method === 'eth_requestAccounts') return [wallet.address]
    assert.equal(method, 'personal_sign')
    assert.equal(String(params[1]).toLowerCase(), wallet.address)
    return wallet.sign(params[0].startsWith('0x') ? Buffer.from(params[0].slice(2), 'hex').toString('utf8') : params[0])
  })
  await target.addInitScript(name => {
    const detail = {
      info: { uuid: '00000000-0000-4000-8000-000000000001', name, rdns: 'test.vessel.synthetic',
        icon: 'data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg"/>' },
      provider: { request: ({ method, params }) => window.syntheticWalletRequest(method, params), on() {}, removeListener() {} },
    }
    const announce = () => window.dispatchEvent(new CustomEvent('eip6963:announceProvider', { detail }))
    window.addEventListener('eip6963:requestProvider', announce)
    announce()
  }, name)
}

/** Signs in through the real sign-in screen with the wallet `exposeSyntheticWallet` announced, up to the Overview. */
async function signInWithSyntheticWallet(page, url, name = 'Synthetic wallet') {
  await page.goto(url)
  await page.getByRole('button', { name: new RegExp(name) }).click()
  await page.getByRole('button', { name: `Sign with ${name}`, exact: true }).click()
  await page.getByRole('region', { name: 'Overview summary', exact: true }).waitFor()
}

module.exports = { signer, exposeSyntheticWallet, signInWithSyntheticWallet }
if (require.main === module) {
  let input = ''
  process.stdin.setEncoding('utf8').on('data', chunk => { input += chunk }).on('end', async () => {
    try {
      const request = JSON.parse(input)
      const wallet = await signer(request.wallet)
      console.log(JSON.stringify({ address: wallet.address, ...request.message ? { signature: wallet.sign(request.message) } : {} }))
    } catch (error) { console.error(error.message); process.exitCode = 1 }
  })
}
