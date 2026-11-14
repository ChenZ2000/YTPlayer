// Read-only comparison with the user's reference checkout; never installs or writes there.
// Usage: node Tests/Auth/upstream_vectors.cjs D:/GitHub/api-enhanced
const fs = require('fs')
const path = require('path')
const vm = require('vm')
const { createRequire } = require('module')
const entry = path.resolve(process.argv[2], 'util/crypto.js')
const upstreamRequire = createRequire(entry)
const sandbox = {
  module: { exports: {} }, Buffer, TextDecoder, console,
  // Compression is outside these AES/RSA request vectors. A missing new upstream
  // compression dependency must not require modifying the reference checkout.
  require: name => name === './zstd' ? new Proxy({}, { get() { throw new Error('Compression not exercised by these vectors') } }) : upstreamRequire(name),
}
vm.runInNewContext(fs.readFileSync(entry, 'utf8'), sandbox)
vm.runInNewContext('Math.random=()=>0', sandbox)
const data = { phone: '13800000000', countrycode: '86', captcha: '123456', type: '1', https: 'true', remember: 'true', secureCaptcha: '' }
const crypto = sandbox.module.exports
const result = { data, weapi: crypto.weapi(data), eapi: crypto.eapi('/api/w/login/cellphone', data) }
result.unicodeData = { message: '登录验证✓', nested: { ids: '[1,2]', enabled: true } }
result.unicodeSecret = '0123456789abcdef'
sandbox.secret = result.unicodeSecret
vm.runInNewContext("let offset=0;const alphabet='abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';Math.random=()=>alphabet.indexOf(secret[offset++])/61", sandbox)
result.unicodeWeapi = crypto.weapi(result.unicodeData)
process.stdout.write(JSON.stringify(result, null, 2) + '\n')
