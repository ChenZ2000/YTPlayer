#!/usr/bin/env node
'use strict'

const fs = require('fs')
const path = require('path')
const https = require('https')
const { randomBytes } = require('crypto')
const { URL } = require('url')

const DEFAULT_ACCOUNT_PATH = path.resolve(
  __dirname,
  '..',
  'bin',
  'Debug',
  'account.json',
)
const DEFAULT_API_ROOT = path.resolve(
  __dirname,
  '..',
  '..',
  'NeteaseCloudMusicApi',
)
const DEFAULT_USER_AGENT =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36'
const DEFAULT_TOP_TRACKS = 10

async function main() {
  const cli = parseArgs(process.argv.slice(2))
  if (cli.help) {
    printHelp()
    return
  }

  const accountPath = resolveInputPath(cli.accountPath, DEFAULT_ACCOUNT_PATH)
  const apiRoot = resolveInputPath(cli.apiRoot, DEFAULT_API_ROOT)
  const cryptoImpl = loadCryptoModule(apiRoot)

  const account = readAccountFile(accountPath)
  const cookieJar = buildCookieJar(account)
  const userAgent =
    cli.userAgent ||
    account.DesktopUserAgent ||
    account.DeviceUserAgent ||
    DEFAULT_USER_AGENT
  const userId = normalizeUserId(account.UserId || account.userId)
  if (!userId) {
    throw new Error(
      '无法确定用户 ID，请确认 account.json 中包含 UserId 字段。',
    )
  }

  const client = new NeteaseWeapiClient({
    cookieJar,
    userAgent,
    cryptoImpl,
    verbose: cli.verbose,
  })

  const detail = await safeCall(
    () => client.userDetail(userId),
    '用户详情',
    cli.verbose,
  )
  const allTimeRecord = await safeCall(
    () => client.userRecord(userId, 0),
    '所有时间的听歌记录',
    cli.verbose,
  )
  const weekRecord = await safeCall(
    () => client.userRecord(userId, 1),
    '最近一周的听歌记录',
    cli.verbose,
  )

  const report = buildReport({
    accountPath,
    account,
    detail,
    allTimeRecord,
    weekRecord,
    top: cli.top ?? DEFAULT_TOP_TRACKS,
  })

  if (cli.json) {
    console.log(JSON.stringify(report, null, 2))
  } else {
    printHumanSummary(report)
  }
}

class NeteaseWeapiClient {
  constructor({ cookieJar, userAgent, cryptoImpl, verbose = false }) {
    this.cookieJar = { ...cookieJar }
    this.cookieString = cookieJarToString(this.cookieJar)
    this.csrfToken =
      this.cookieJar.__csrf ||
      this.cookieJar._csrf ||
      this.cookieJar.csrf_token ||
      ''
    this.userAgent = userAgent
    this.cryptoImpl = cryptoImpl
    this.verbose = verbose
  }

  async callWeapi(url, body = {}) {
    const data = { ...body }
    if (!data.csrf_token && this.csrfToken) {
      data.csrf_token = this.csrfToken
    }
    const encrypted = this.cryptoImpl.weapi(data)
    const targetUrl = new URL(url.replace(/\w*api/, 'weapi'))
    const payload = new URLSearchParams(encrypted).toString()
    const headers = {
      Accept: 'application/json, text/plain, */*',
      'Content-Type': 'application/x-www-form-urlencoded',
      Referer: 'https://music.163.com',
      Cookie: this.cookieString,
      'User-Agent': this.userAgent,
    }
    return performHttpPost(targetUrl, payload, headers, this.verbose)
  }

  userDetail(uid) {
    return this.callWeapi(
      `https://music.163.com/weapi/v1/user/detail/${uid}`,
      {},
    )
  }

  userRecord(uid, type = 0) {
    return this.callWeapi('https://music.163.com/weapi/v1/play/record', {
      uid,
      type,
    })
  }
}

function performHttpPost(url, body, headers, verbose) {
  return new Promise((resolve, reject) => {
    const options = {
      hostname: url.hostname,
      port: url.port || 443,
      path: `${url.pathname}${url.search || ''}`,
      method: 'POST',
      headers: {
        ...headers,
        'Content-Length': Buffer.byteLength(body),
      },
    }
    const req = https.request(options, (res) => {
      const chunks = []
      res.on('data', (chunk) => chunks.push(chunk))
      res.on('end', () => {
        const raw = Buffer.concat(chunks).toString('utf8')
        let parsed
        try {
          parsed = raw ? JSON.parse(raw) : {}
        } catch (error) {
          const err = new Error(`无法解析 ${url.href} 的应答`)
          err.rawBody = raw
          reject(err)
          return
        }
        const successCode =
          typeof parsed.code === 'number' ? parsed.code : res.statusCode
        if (verbose) {
          console.error(
            `[DEBUG] ${url.href} -> HTTP ${res.statusCode}, API code ${successCode}`,
          )
        }
        if (res.statusCode !== 200 || successCode !== 200) {
          const err = new Error(
            `请求 ${url.href} 失败 (http=${res.statusCode}, code=${successCode})`,
          )
          err.response = parsed
          reject(err)
          return
        }
        resolve(parsed)
      })
    })
    req.on('error', reject)
    req.write(body)
    req.end()
  })
}

function resolveInputPath(input, fallback) {
  if (!input) return fallback
  return path.isAbsolute(input)
    ? input
    : path.resolve(process.cwd(), input.trim())
}

function loadCryptoModule(apiRoot) {
  const cryptoPath = path.join(apiRoot, 'util', 'crypto.js')
  if (!fs.existsSync(cryptoPath)) {
    throw new Error(
      `无法找到 util/crypto.js，当前 apiRoot = ${apiRoot}，请使用 --api-root 指定 NeteaseCloudMusicApi 的位置`,
    )
  }
  return require(cryptoPath)
}

function readAccountFile(accountPath) {
  if (!fs.existsSync(accountPath)) {
    throw new Error(`无法找到账号文件: ${accountPath}`)
  }
  const raw = fs.readFileSync(accountPath, 'utf8')
  try {
    return JSON.parse(raw)
  } catch (error) {
    throw new Error(`解析账号文件失败: ${error.message}`)
  }
}

function normalizeUserId(value) {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string' && value.trim()) {
    const num = Number(value.trim())
    return Number.isFinite(num) ? num : null
  }
  return null
}

function parseCookieString(cookieString) {
  if (typeof cookieString !== 'string' || cookieString.length === 0) {
    return {}
  }
  return cookieString.split(';').reduce((acc, part) => {
    const segment = part.trim()
    if (!segment) return acc
    const separator = segment.indexOf('=')
    if (separator === -1) {
      acc[segment] = ''
    } else {
      const key = segment.slice(0, separator).trim()
      const value = segment.slice(separator + 1).trim()
      if (key) acc[key] = value
    }
    return acc
  }, {})
}

function buildCookieJar(account) {
  const jar = {}
  if (Array.isArray(account.Cookies)) {
    account.Cookies.forEach((cookie) => {
      if (cookie?.Name) {
        jar[cookie.Name] = cookie.Value || ''
      }
    })
  }
  Object.assign(jar, parseCookieString(account.Cookie))
  const fieldMappings = [
    ['MusicU', 'MUSIC_U'],
    ['MusicA', 'MUSIC_A'],
    ['CsrfToken', '__csrf'],
    ['CsrfToken', '_csrf'],
    ['NmtId', 'NMTID'],
    ['NtesNuid', '_ntes_nuid'],
    ['DeviceId', 'deviceId'],
    ['SDeviceId', 'sDeviceId'],
    ['DeviceOs', 'os'],
    ['DeviceOsVersion', 'osver'],
    ['DeviceAppVersion', 'appver'],
    ['DeviceBuildVersion', 'buildver'],
    ['DeviceChannel', 'channel'],
    ['DeviceResolution', 'resolution'],
    ['DeviceMobileName', 'mobilename'],
  ]
  fieldMappings.forEach(([source, target]) => {
    if (account[source] && !jar[target]) {
      jar[target] = account[source]
    }
  })
  jar.__remember_me =
    jar.__remember_me === undefined ? 'true' : String(jar.__remember_me)
  if (!jar._ntes_nuid) {
    jar._ntes_nuid = randomBytes(16).toString('hex')
  }
  return jar
}

function cookieJarToString(jar) {
  return Object.entries(jar)
    .filter(([, value]) => value !== undefined && value !== null)
    .map(
      ([key, value]) =>
        `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`,
    )
    .join('; ')
}

async function safeCall(task, label, verbose) {
  try {
    return await task()
  } catch (error) {
    console.error(`[WARN] ${label} 获取失败: ${error.message}`)
    if (verbose && error.response) {
      console.error(
        `[DEBUG] ${label} error payload: ${JSON.stringify(
          error.response,
          null,
          2,
        )}`,
      )
    }
    return null
  }
}

function buildReport({
  accountPath,
  account,
  detail,
  allTimeRecord,
  weekRecord,
  top,
}) {
  const summary = {
    generatedAt: new Date().toISOString(),
    accountFile: accountPath,
    user: {
      id: account.UserId || account.userId || null,
      nickname:
        account.Nickname ||
        detail?.profile?.nickname ||
        (detail?.profile ? detail.profile.nickname : null) ||
        null,
      avatar: account.AvatarUrl || detail?.profile?.avatarUrl || null,
    },
    listenSongs: detail?.listenSongs ?? null,
    level: detail?.level ?? detail?.profile?.level ?? null,
    totals: {
      allTime: summarizeRecordSet(allTimeRecord?.allData, top),
      lastWeek: summarizeRecordSet(weekRecord?.weekData, top),
    },
    sourceCodes: {
      detailCode: detail?.code ?? null,
      allTimeCode: allTimeRecord?.code ?? null,
      lastWeekCode: weekRecord?.code ?? null,
    },
  }
  return summary
}

function summarizeRecordSet(records, maxTopEntries = DEFAULT_TOP_TRACKS) {
  if (!Array.isArray(records) || records.length === 0) {
    return null
  }
  let totalDurationMs = 0
  let totalPlayCount = 0
  const tracks = records.map((record) => {
    const summary = buildTrackSummary(record)
    totalDurationMs += summary.totalListenMs
    totalPlayCount += summary.playCount
    return summary
  })
  tracks.sort((a, b) => b.totalListenMs - a.totalListenMs)
  const uniqueTrackCount = tracks.length
  const averageDurationPerTrackMs = uniqueTrackCount
    ? Math.floor(totalDurationMs / uniqueTrackCount)
    : 0
  return {
    uniqueTrackCount,
    totalPlayCount,
    totalDurationMs,
    totalDurationReadable: formatDuration(totalDurationMs),
    averagePlayCount: uniqueTrackCount
      ? Number((totalPlayCount / uniqueTrackCount).toFixed(2))
      : 0,
    averageDurationPerTrackMs,
    averageDurationPerTrackReadable: formatDuration(
      averageDurationPerTrackMs,
    ),
    topTracks: tracks.slice(
      0,
      Math.max(0, Number.isFinite(maxTopEntries) ? maxTopEntries : 0),
    ),
  }
}

function buildTrackSummary(entry) {
  const song = entry.song || entry
  const playCount =
    Number(entry.playCount ?? entry.playcount ?? entry.playTimes ?? 0) || 0
  const durationMs =
    Number(song?.dt ?? song?.duration ?? entry.duration ?? 0) || 0
  const totalListenMs = playCount * durationMs
  const artists = Array.isArray(song?.ar)
    ? song.ar.map((artist) => artist.name).filter(Boolean).join('/')
    : Array.isArray(song?.artists)
    ? song.artists.map((artist) => artist.name).filter(Boolean).join('/')
    : null
  return {
    id: song?.id ?? entry.songId ?? null,
    name: song?.name || entry.songName || 'Unknown',
    artists,
    album: song?.al?.name || song?.album?.name || null,
    durationMs,
    durationReadable: formatDuration(durationMs),
    playCount,
    totalListenMs,
    totalListenReadable: formatDuration(totalListenMs),
  }
}

function formatDuration(durationMs) {
  if (!Number.isFinite(durationMs) || durationMs <= 0) return '0s'
  const totalSeconds = Math.floor(durationMs / 1000)
  const days = Math.floor(totalSeconds / 86400)
  const hours = Math.floor((totalSeconds % 86400) / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60
  const parts = []
  if (days) parts.push(`${days}d`)
  if (hours) parts.push(`${hours}h`)
  if (minutes) parts.push(`${minutes}m`)
  if (seconds || parts.length === 0) parts.push(`${seconds}s`)
  return parts.join(' ')
}

function printHumanSummary(report) {
  const userLine = `${report.user.nickname || '未知昵称'} (${
    report.user.id || '未知 ID'
  })`
  console.log(`网易云听歌统计 - ${userLine}`)
  if (report.listenSongs !== null) {
    console.log(`网易云记录的累计听歌曲目：${formatNumber(report.listenSongs)} 首`)
  }
  if (report.level !== null) {
    console.log(`当前等级：Lv.${report.level}`)
  }
  printRecordSummary('所有时间', report.totals.allTime)
  printRecordSummary('最近一周', report.totals.lastWeek)
  if (report.totals.allTime?.topTracks?.length) {
    console.log('')
    console.log(`按聆听时长排序的前 ${Math.min(
      report.totals.allTime.topTracks.length,
      5,
    )} 首歌曲：`)
    report.totals.allTime.topTracks.slice(0, 5).forEach((track, index) => {
      console.log(
        `${index + 1}. ${track.name} - ${track.artists || '未知歌手'} | ${
          track.playCount
        } 次 (${track.totalListenReadable})`,
      )
    })
  }
  console.log('\n如需 JSON 结果，请追加 --json 参数运行本脚本。')
}

function printRecordSummary(label, record) {
  if (!record) {
    console.log(`${label}：暂无可用数据`)
    return
  }
  console.log(
    `${label}：${record.totalDurationReadable}，${formatNumber(
      record.totalPlayCount,
    )} 次播放 / ${formatNumber(record.uniqueTrackCount)} 首歌曲`,
  )
}

function formatNumber(value) {
  if (!Number.isFinite(value)) return '0'
  return value.toLocaleString('zh-CN')
}

function parseArgs(argv) {
  const options = {}
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i]
    switch (arg) {
      case '-a':
      case '--account':
        options.accountPath = argv[++i]
        break
      case '--api-root':
        options.apiRoot = argv[++i]
        break
      case '--ua':
      case '--user-agent':
        options.userAgent = argv[++i]
        break
      case '--top':
        options.top = Number(argv[++i])
        break
      case '--json':
        options.json = true
        break
      case '--verbose':
        options.verbose = true
        break
      case '-h':
      case '--help':
        options.help = true
        break
      default:
        if (!arg.startsWith('-') && !options.accountPath) {
          options.accountPath = arg
        }
        break
    }
  }
  if (!Number.isFinite(options.top) || options.top <= 0) {
    options.top = DEFAULT_TOP_TRACKS
  }
  return options
}

function printHelp() {
  console.log(`网易云听歌时长统计

用法:
  node Scripts/fetchListeningDurations.js [选项]

常用选项:
  -a, --account <path>   指定 account.json 文件路径 (默认: ${DEFAULT_ACCOUNT_PATH})
      --api-root <path>  指定 NeteaseCloudMusicApi 根目录 (默认: ${DEFAULT_API_ROOT})
      --top <n>          输出的 Top 歌曲数量 (默认: ${DEFAULT_TOP_TRACKS})
      --json             以 JSON 形式输出结果
      --verbose          打印额外调试与错误信息
      --ua <value>       覆盖默认的 User-Agent
  -h, --help             展示本帮助信息
`)
}

main().catch((error) => {
  console.error(`[ERROR] ${error.message}`)
  process.exitCode = 1
})
