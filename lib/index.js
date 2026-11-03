/**
 * dsh-pet-float —— 把 DSH 里的宠物变成桌面悬浮球（宿主插件）
 *
 * 结构（契约见 ../SPEC.md）：
 *   宿主插件（本文件，跑在 DSH 宿主 node 进程里）
 *     ├─ 监听 session/event 与 agent/assistant-stream，维护每会话状态
 *     ├─ 把状态原子写入 %DSH_HOME%\.dshp-state.json（桌面伴侣只读这个文件）
 *     ├─ 读 %DSH_HOME%\.dshp-cmd.json（桌面伴侣写的"打开某会话"命令）
 *     ├─ 提供 /dsh-pet/* 路由（页面桥带 cookie 访问）
 *     └─ spawn 桌面伴侣 DshPetFloat.exe
 *   页面桥 assets/pet-bridge.js（注入 DSH 页面）
 *     ├─ 读 DOM：当前会话标题、当前思考摘要
 *     └─ 每 800ms POST /dsh-pet/page（心跳 + 取命令），执行"切换会话"
 *
 * 为什么状态走文件而不是让伴侣直接访问 HTTP：DSH 的 connection 插件给所有
 * 控制台请求挂了浏览器鉴权 401（见 @deepseek-ai/dsh-client-connection 的 BrowserAuth），
 * 外部进程拿不到那个签名 cookie。所以伴侣只碰文件，网络只走页面桥。
 */
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { createPetEngine, oneLine } from './pet.mjs'
import { createWalletTracker } from './wallet.mjs'

const PACKAGE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const DSH_HOME = process.env.DSH_HOME || path.join(os.homedir(), '.dsh')

const STATE_FILE = path.join(DSH_HOME, '.dshp-state.json')
const CMD_FILE = path.join(DSH_HOME, '.dshp-cmd.json')
const WALLET_FILE = path.join(DSH_HOME, '.dshp-wallet.json')
/** 用户主动退出伴侣的标记（伴侣写、伴侣启动时删、插件启动时也删） */
const QUIT_FLAG = process.env.DSH_PET_QUIT_FLAG || path.join(
  process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'),
  'dsh-pet',
  'quit.flag',
)
const ROUTE_PREFIX = '/dsh-pet/'
const BRIDGE_PATH = '/dsh-pet/pet-bridge.js'
const VERSION = '1.1.0'

/** 桌面端唯一能生效的注入通道：结构化行 + 内联 script（不让页面 boot 因我们失败） */
const DESKTOP_BRIDGE_ROW =
  '(function(){try{var d=document.body||document.head||document.documentElement;if(!d)return;' +
  'if(window.__dshPetBridgeLoaded)return;window.__dshPetBridgeLoaded=1;' +
  'var s=document.createElement("script");s.src="' + BRIDGE_PATH + '";s.async=true;' +
  's.onerror=function(){};d.appendChild(s)}catch(e){}})()'

const TAG_NAME = 'dsh-pet-float'
const PET_DIR = path.join(DSH_HOME, 'dsh-pet')

/** 余额轮询间隔（毫秒）；余额不常变，60s 足够，失败不重试得太凶 */
const WALLET_INTERVAL_MS = 60 * 1000
/** 一轮结束后延迟多久再拉余额（平台侧扣费有几十毫秒~几秒的延迟） */
const WALLET_AFTER_TURN_MS = 3000

/**
 * 读余额。两条路，与鲸鱼挂件同口径（SPEC 3.5.1）：
 *   1) DSH 账号登录态：ctx.get('deepseekAccount').getBalance(...)
 *      ⚠️ 必须用 ctx.get 读可选服务，绝不能写进 inject（老宿主没这服务会让插件完全不 apply）
 *   2) API key：ctx.credentials.resolve('DEEPSEEK_API_KEY') → https://api.deepseek.com/user/balance
 * 两条路互不兜底；都失败返回 { ok:false, error }。
 */
async function readBalance(ctx) {
  const errors = []

  try {
    const account = typeof ctx.get === 'function' ? ctx.get('deepseekAccount') : null
    if (account && typeof account.getBalance === 'function') {
      const result = await account.getBalance({
        version: String(process.env.DSH_CLIENT_VERSION || process.env.DSH_VERSION || '') || 'unknown',
        locale: String(process.env.DSH_LOCALE || '') || 'zh_CN',
        timezoneOffsetSeconds: -new Date().getTimezoneOffset() * 60,
      })
      if (result && result.status === 'ready') {
        // 钱包拆成充值(normal_wallets → value)与赠金(bonusWallets)，两边都要看：
        // 新用户常常只有赠金，只看 value 会误判成"没有余额"。
        const norm = Array.isArray(result.value) ? result.value : []
        const bonus = Array.isArray(result.bonusWallets) ? result.bonusWallets : []
        const wallets = norm.concat(bonus).filter((w) => w && Number.isFinite(Number(w.balance)))
        if (wallets.length === 0) {
          errors.push(norm.length || bonus.length ? '钱包字段不可用' : '账号没有余额钱包')
        } else {
          const curOf = (w) => String((w && w.currency) || 'CNY').toUpperCase()
          const nonzero = wallets.filter((w) => Number(w.balance) > 0)
          const picked = nonzero.find((w) => curOf(w) === 'CNY') || nonzero[0]
            || wallets.find((w) => curOf(w) === 'CNY') || wallets[0]
          const currency = curOf(picked)
          const total = wallets
            .filter((w) => curOf(w) === currency)
            .reduce((sum, w) => sum + Number(w.balance), 0)
          return { ok: true, balance: total, currency, source: 'account' }
        }
      } else if (result) {
        errors.push('账号余额接口未就绪（' + String(result.status || 'unknown') + '）')
      } else {
        errors.push('未登录 DeepSeek 账号')
      }
    } else {
      errors.push('当前 DSH 没有账号服务')
    }
  } catch (err) {
    errors.push('账号余额接口调用失败：' + String((err && err.message) || err).slice(0, 80))
  }

  try {
    const credentials = typeof ctx.get === 'function' ? ctx.get('credentials') : null
    const key = credentials && typeof credentials.resolve === 'function'
      ? await credentials.resolve('DEEPSEEK_API_KEY')
      : null
    if (key) {
      const response = await fetch('https://api.deepseek.com/user/balance', {
        headers: { Authorization: 'Bearer ' + String(key) },
        signal: AbortSignal.timeout(6000),
      })
      if (response.ok) {
        const body = await response.json()
        const info = Array.isArray(body && body.balance_infos) ? body.balance_infos[0] : null
        const amount = info && Number(info.total_balance)
        if (Number.isFinite(amount)) {
          return { ok: true, balance: amount, currency: String(info.currency || 'CNY').toUpperCase(), source: 'api-key' }
        }
        errors.push('余额接口返回里没有 total_balance')
      } else {
        errors.push('余额接口 HTTP ' + response.status)
      }
    } else {
      errors.push('没有 DEEPSEEK_API_KEY')
    }
  } catch (err) {
    errors.push('API key 查余额失败：' + String((err && err.message) || err).slice(0, 80))
  }

  return { ok: false, error: errors[0] || '取不到余额' }
}

/**
 * 首次运行自举：把插件包里的伴侣 exe 与默认素材复制到 $DSH_HOME/dsh-pet。
 * 这样从插件市场装完、重启 DSH 就能直接用，不必手动跑 install.mjs。
 * 只补缺失的文件，不覆盖用户自己放的 avatar.png / sound.wav。
 */
function bootstrapFiles() {
  // 仓库里源码在 companion/，开发树里伴侣源码与 plugin/ 平级——两处都找
  const companionDirs = [
    path.join(PACKAGE_ROOT, 'companion'),
    path.join(PACKAGE_ROOT, '..', 'companion'),
  ]
  const firstExe = (() => {
    for (const dir of companionDirs) {
      const p = path.join(dir, 'bin', 'DshPetFloat.exe')
      try {
        if (fs.statSync(p).isFile()) return p
      } catch (err) {}
    }
    return path.join(companionDirs[0], 'bin', 'DshPetFloat.exe')
  })()
  const tasks = [
    // [源, 目标, 大小不同也覆盖]——exe 要跟着插件版本升级，素材只补缺不覆盖用户自备的
    [firstExe, path.join(PET_DIR, 'DshPetFloat.exe'), true],
    [path.join(PACKAGE_ROOT, 'assets', 'pet.png'), path.join(PET_DIR, 'avatar.png'), false],
    [path.join(PACKAGE_ROOT, 'assets', 'ah.wav'), path.join(PET_DIR, 'sound.wav'), false],
    [path.join(PACKAGE_ROOT, 'assets', 'pet.wav'), path.join(PET_DIR, 'pet.wav'), false],
  ]
  try { fs.mkdirSync(PET_DIR, { recursive: true }) } catch (err) { return }
  for (const [from, to, upgrade] of tasks) {
    try {
      if (!fs.statSync(from).isFile()) continue
    } catch (err) { continue }
    let need = false
    try {
      const st = fs.statSync(to)
      need = upgrade ? (!st.isFile() || st.size !== fs.statSync(from).size) : !st.isFile()
    } catch (err) {
      need = true
    }
    if (!need) continue
    try {
      fs.copyFileSync(from, to)
    } catch (err) {
      // 伴侣正在跑时 exe 被占用：忽略，下次 DSH 启动再补
    }
  }
}

/** 角色图：用户自备 > $DSH_HOME 副本 > 包内默认（代码生成，CC0）> 本机有鲸鱼挂件就蹭它的图 */
function findAvatar() {
  const candidates = [
    process.env.DSH_PET_AVATAR,
    path.join(PET_DIR, 'avatar.png'),
    path.join(PACKAGE_ROOT, 'assets', 'pet.png'),
    path.join(PACKAGE_ROOT, 'assets', 'avatar.png'),
  ]
  const profilesDir = path.join(DSH_HOME, 'profiles')
  try {
    for (const name of fs.readdirSync(profilesDir)) {
      candidates.push(path.join(profilesDir, name, 'node_modules', 'dsh-whale-widget-bowl', 'assets', 'DSniang1.png'))
    }
  } catch (err) {}
  for (const candidate of candidates) {
    if (!candidate) continue
    try {
      if (fs.statSync(candidate).isFile()) return candidate
    } catch (err) {}
  }
  return ''
}

/** 结束/停滞提示音：用户自备 > $DSH_HOME 副本 > 包内默认（合成「啊」，CC0）> 本机有鲸鱼挂件就蹭它的音效 */
function findSound() {
  const candidates = [
    process.env.DSH_PET_SOUND,
    path.join(PET_DIR, 'sound.wav'),
    path.join(PACKAGE_ROOT, 'assets', 'ah.wav'),
    path.join(PACKAGE_ROOT, 'assets', 'sound.wav'),
  ]
  const profilesDir = path.join(DSH_HOME, 'profiles')
  try {
    for (const name of fs.readdirSync(profilesDir)) {
      const assets = path.join(profilesDir, name, 'node_modules', 'dsh-whale-widget-bowl', 'assets')
      candidates.push(path.join(assets, 'task-end-a.wav'))
      candidates.push(path.join(assets, 'minecraft-exp-orb.wav'))
    }
  } catch (err) {}
  for (const candidate of candidates) {
    if (!candidate) continue
    try {
      if (fs.statSync(candidate).isFile()) return candidate
    } catch (err) {}
  }
  return ''
}

/** 捏捏音效：用户自备 > $DSH_HOME 副本 > 包内默认（合成「捏」），找不到就静音 */
function findPetSound() {
  const candidates = [
    process.env.DSH_PET_PET_SOUND,
    path.join(PET_DIR, 'pet.wav'),
    path.join(PACKAGE_ROOT, 'assets', 'pet.wav'),
  ]
  for (const candidate of candidates) {
    if (!candidate) continue
    try {
      if (fs.statSync(candidate).isFile()) return candidate
    } catch (err) {}
  }
  return ''
}

/** 伴侣 exe 位置 */
function findCompanion() {
  const candidates = [
    process.env.DSH_PET_EXE,
    path.join(PET_DIR, 'DshPetFloat.exe'),
    path.join(PACKAGE_ROOT, 'companion', 'bin', 'DshPetFloat.exe'),
    path.join(PACKAGE_ROOT, 'companion', 'DshPetFloat.exe'),
    path.join(PACKAGE_ROOT, 'vendor', 'DshPetFloat.exe'),
  ]
  for (const candidate of candidates) {
    if (!candidate) continue
    try {
      if (fs.statSync(candidate).isFile()) return candidate
    } catch (err) {}
  }
  return ''
}

function readBody(req, limit = 64 * 1024) {
  return new Promise((resolve) => {
    let size = 0
    const chunks = []
    let done = false
    const finish = (value) => {
      if (done) return
      done = true
      resolve(value)
    }
    req.on('data', (chunk) => {
      size += chunk.length
      if (size > limit) {
        finish('')
        req.destroy()
        return
      }
      chunks.push(chunk)
    })
    req.on('end', () => finish(Buffer.concat(chunks).toString('utf8')))
    req.on('error', () => finish(''))
  })
}

/** 原子写：先写 .tmp 再 rename，伴侣永远读不到半个 JSON */
function writeFileAtomic(file, text) {
  const tmp = file + '.tmp'
  fs.writeFileSync(tmp, text, 'utf8')
  try {
    fs.renameSync(tmp, file)
  } catch (err) {
    // Windows 上杀软/编辑器偶发占用：重试一次，再不行就直写
    try {
      fs.renameSync(tmp, file)
    } catch (err2) {
      fs.writeFileSync(file, text, 'utf8')
      try { fs.unlinkSync(tmp) } catch (err3) {}
    }
  }
}

export default {
  name: TAG_NAME,
  apply(root) {
    const disposers = []
    let disposed = false
    let spawnedChild = null
    const bridgeFile = path.join(PACKAGE_ROOT, 'assets', 'pet-bridge.js')
    let bridgeCache = { mtime: -1, text: '' }

    // 首次运行把伴侣 exe 与默认素材铺到 $DSH_HOME/dsh-pet（从市场装完即可用）
    bootstrapFiles()

    function loadBridge() {
      try {
        const stat = fs.statSync(bridgeFile)
        const mtime = stat.mtimeMs
        if (bridgeCache.mtime !== mtime) {
          bridgeCache = { mtime, text: fs.readFileSync(bridgeFile, 'utf8') }
        }
        return bridgeCache.text
      } catch (err) {
        return ''
      }
    }

    // ① 结构化注入行：官方桌面端（Electron）唯一生效的注入通道。
    //    必须**立刻**注册（桌面端的注入表是宿主启动时一次性收集的），所以放在 inject 之前。
    disposers.push(root.on('webserver/index-inject', (table) => {
      try {
        if (!Array.isArray(table)) return
        for (const row of table) {
          if (!row) continue
          if (row.kind === 'script-src' && row.src === BRIDGE_PATH) return
          if (row.kind === 'script' && typeof row.text === 'string' && row.text.indexOf(BRIDGE_PATH) >= 0) return
        }
        table.push({ kind: 'script', placement: 'body', text: DESKTOP_BRIDGE_ROW })
      } catch (err) {}
    }))

    root.effect(() => () => {
      disposed = true
      for (const dispose of disposers) {
        try { dispose() } catch (err) {}
      }
      try { if (spawnedChild && spawnedChild.pid) spawnedChild.kill() } catch (err) {}
    })

    root.inject(['webServer'], (ctx) => {
      const engine = createPetEngine({
        resolveTitle: (session) => {
          try {
            const service = typeof ctx.get === 'function' ? ctx.get('sessionTitle') : null
            if (!service || typeof service.get !== 'function') return ''
            const snap = service.get(session)
            const title = typeof snap === 'string' ? snap : (snap && (snap.title || snap.text))
            return oneLine(title, 120)
          } catch (err) {
            return ''
          }
        },
      })

      const avatarFile = findAvatar()
      const soundFile = findSound()
      const petSoundFile = findPetSound()
      const electronPid = (() => {
        const pid = Number(process.ppid || 0)
        return Number.isFinite(pid) ? pid : 0
      })()
      const port = (() => {
        try { return Number(ctx.webServer.port || 0) } catch (err) { return 0 }
      })()
      engine.setHostInfo({
        version: VERSION,
        pid: process.pid,
        electronPid,
        port,
        avatarFile,
        soundFile,
        petSoundFile: petSoundFile,
        stateFile: STATE_FILE,
        cmdFile: CMD_FILE,
      })

      // —— 钱包：余额 + 今日花费（余额差分日账本） + 今日 token ——
      const wallet = createWalletTracker({
        file: WALLET_FILE,
        readBalance: () => readBalance(ctx),
        log: (level, message) => {
          try {
            if (level === 'warn' && ctx.logger && typeof ctx.logger.warn === 'function') ctx.logger.warn('[dsh-pet] ' + message)
            else if (ctx.logger && typeof ctx.logger.info === 'function') ctx.logger.info('[dsh-pet] ' + message)
          } catch (err) {}
        },
      })
      let walletTimer = null
      const scheduleWalletRefresh = (delayMs) => {
        if (walletTimer) return
        walletTimer = setTimeout(() => {
          walletTimer = null
          wallet.refresh().then(() => { try { scheduleFlush() } catch (err) {} }).catch(() => {})
        }, Math.max(0, Number(delayMs) || 0))
        if (walletTimer && typeof walletTimer.unref === 'function') walletTimer.unref()
      }
      disposers.push(() => {
        if (walletTimer) {
          clearTimeout(walletTimer)
          walletTimer = null
        }
      })

      // —— 落盘：内容变化后 200ms 合并写一次；无变化时 1s 心跳刷 seq/ts ——
      let flushTimer = null
      let lastJson = ''
      let writeError = ''

      function flush(heartbeat) {
        if (flushTimer) {
          clearTimeout(flushTimer)
          flushTimer = null
        }
        try {
          engine.prune(Date.now())
          const snapshot = engine.snapshot({ wallet: wallet.current() })
          const json = JSON.stringify(snapshot)
          if (!heartbeat && json === lastJson) return
          writeFileAtomic(STATE_FILE, json)
          lastJson = json
          writeError = ''
        } catch (err) {
          const message = String((err && err.message) || err)
          if (message !== writeError) {
            writeError = message
            try { ctx.logger.warn('[dsh-pet] 状态写盘失败：' + message) } catch (err2) {}
          }
        }
      }

      function scheduleFlush() {
        if (flushTimer) return
        flushTimer = setTimeout(() => {
          flushTimer = null
          flush(false)
        }, 200)
      }

      // 事件监听：会话事件 + 实时思考流
      disposers.push(ctx.on('session/event', (session, event) => {
        try {
          engine.onSessionEvent(session, event)
          // 今日 token：按 assistant/message 的 usage 累加（与引擎里的 turn 用量同源）
          if (event && event.type === 'assistant/message') {
            const data = (event && event.data) || {}
            const usage = data.usage || (data.message && data.message.usage) || null
            if (usage) {
              let total = 0
              for (const raw of [usage.inputTokens, usage.cacheReadTokens || usage.cachedTokens, usage.outputTokens]) {
                const n = Number(raw)
                if (Number.isFinite(n) && n > 0) total += n
              }
              if (total > 0) wallet.addTurnTokens(total)
            }
          }
          // 一轮结束：等平台扣费落账再拉一次余额
          if (event && event.type === 'turn/end') scheduleWalletRefresh(WALLET_AFTER_TURN_MS)
          scheduleFlush()
        } catch (err) {}
      }))
      disposers.push(ctx.on('agent/assistant-stream', (payload) => {
        try {
          engine.onStream(payload)
          scheduleFlush()
        } catch (err) {}
      }))
      disposers.push(ctx.on('session/disposed', () => {
        try { scheduleFlush() } catch (err) {}
      }))

      // 命令文件：伴侣写、页面读。每次页面心跳时按 mtime 判断有没有新命令。
      let cmdCache = { id: 0 }

      function readPendingCommand() {
        let stat
        try {
          stat = fs.statSync(CMD_FILE)
        } catch (err) {
          cmdCache = { id: 0 }
          return null
        }
        if (!stat.isFile()) return null
        let parsed
        try {
          parsed = JSON.parse(fs.readFileSync(CMD_FILE, 'utf8'))
        } catch (err) {
          return null // 伴侣正在写：下次再说
        }
        const id = Number(parsed && parsed.id)
        if (!Number.isFinite(id) || id <= 0) return null
        if (cmdCache.id === id) return null
        cmdCache = { id }
        const action = String(parsed.action || 'open')
        const title = oneLine(parsed.title, 120)
        return { id, action: action === 'focus' ? 'focus' : 'open', title, ts: Number(parsed.ts || Date.now()) }
      }

      function clearCommandFile(id) {
        try {
          const parsed = JSON.parse(fs.readFileSync(CMD_FILE, 'utf8'))
          if (Number(parsed && parsed.id) === Number(id)) fs.unlinkSync(CMD_FILE)
        } catch (err) {}
      }

      // —— 信任栅栏（与鲸鱼挂件同思路，fail-closed）——
      function isLoopbackHostname(hostname) {
        const host = String(hostname || '').toLowerCase().replace(/^\[/, '').replace(/\]$/, '')
        if (!host) return false
        if (host === 'localhost' || host.endsWith('.localhost')) return true
        if (host === '::1') return true
        const match = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host)
        if (!match) return false
        if (Number(match[1]) !== 127) return false
        return [match[2], match[3], match[4]].every((part) => Number(part) <= 255)
      }

      function selfRejected(req) {
        try {
          const headers = req.headers || {}
          const host = String(headers.host || '')
          const authority = host.replace(/:\d+$/, '')
          if (!isLoopbackHostname(authority)) return 403
          if (String(headers['sec-fetch-site'] || '') === 'cross-site') return 403
          const origin = String(headers.origin || '')
          if (origin) {
            try {
              const url = new URL(origin)
              if (url.host !== host) return 403
            } catch (err) {
              return 403
            }
          }
          // 委托宿主栅栏（里面有浏览器鉴权 401）；旧宿主没有这个方法就只靠上面三层
          const connection = typeof ctx.get === 'function' ? ctx.get('connection') : null
          if (connection && typeof connection.requestRejection === 'function') {
            let code
            try {
              code = connection.requestRejection(req)
            } catch (err) {
              return 403
            }
            if (code !== undefined && code !== null && code !== false) {
              return typeof code === 'number' ? code : 403
            }
          }
          return 0
        } catch (err) {
          return 403
        }
      }

      function register(route) {
        const inner = route.handler
        return ctx.webServer.register(Object.assign({}, route, {
          handler: async (req, res) => {
            const code = selfRejected(req)
            if (code) {
              res.writeHead(code, { 'Content-Type': 'text/plain; charset=utf-8' })
              res.end('dsh-pet: request rejected')
              return
            }
            return inner(req, res)
          },
        }))
      }

      function json(res, status, value) {
        const text = JSON.stringify(value)
        res.writeHead(status, {
          'Content-Type': 'application/json; charset=utf-8',
          'Cache-Control': 'no-store',
          'Content-Length': String(Buffer.byteLength(text)),
        })
        res.end(text)
      }

      disposers.push(register({
        kind: 'exact',
        path: '/dsh-pet/ping',
        handler: (req, res) => json(res, 200, { ok: true, pong: Date.now(), version: VERSION }),
      }))

      disposers.push(register({
        kind: 'exact',
        path: '/dsh-pet/state.json',
        handler: (req, res) => {
          try {
            json(res, 200, engine.snapshot({ wallet: wallet.current() }))
          } catch (err) {
            json(res, 500, { ok: false, error: String((err && err.message) || err) })
          }
        },
      }))

      // 钱包快照 + 手动刷新（调试/页面桥用；GET 只读，POST 触发一次拉取）
      disposers.push(register({
        kind: 'exact',
        path: '/dsh-pet/wallet.json',
        handler: async (req, res) => {
          try {
            if (req.method === 'POST') {
              await wallet.refresh()
              scheduleFlush()
            }
            json(res, 200, Object.assign({ ok: true }, wallet.current()))
          } catch (err) {
            json(res, 500, { ok: false, error: String((err && err.message) || err) })
          }
        },
      }))

      disposers.push(register({
        kind: 'exact',
        path: '/dsh-pet/page',
        handler: async (req, res) => {
          let report = null
          try {
            const text = await readBody(req)
            report = text ? JSON.parse(text) : {}
          } catch (err) {
            report = {}
          }
          try {
            engine.onPageReport(report)
            scheduleFlush()
            const cmd = readPendingCommand()
            json(res, 200, { ok: true, cmd, ts: Date.now() })
          } catch (err) {
            json(res, 500, { ok: false, error: String((err && err.message) || err) })
          }
        },
      }))

      disposers.push(register({
        kind: 'exact',
        path: '/dsh-pet/cmd-ack',
        handler: async (req, res) => {
          let body = null
          try {
            const text = await readBody(req)
            body = text ? JSON.parse(text) : {}
          } catch (err) {
            body = {}
          }
          const id = Number(body && body.id)
          try {
            if (Number.isFinite(id)) clearCommandFile(id)
            json(res, 200, { ok: true })
          } catch (err) {
            json(res, 200, { ok: true })
          }
        },
      }))

      disposers.push(register({
        kind: 'exact',
        path: BRIDGE_PATH,
        handler: (req, res) => {
          const text = loadBridge()
          if (!text) {
            res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' })
            res.end('pet-bridge.js unavailable')
            return
          }
          res.writeHead(200, {
            'Content-Type': 'text/javascript; charset=utf-8',
            'Cache-Control': 'no-store',
            'Content-Length': String(Buffer.byteLength(text)),
          })
          res.end(text)
        },
      }))

      // ② Web 形态（dsh web）：tapIndex 注入（桌面端走 ①，两条并存、各自去重）
      try {
        if (typeof ctx.webServer.tapIndex === 'function') {
          disposers.push(ctx.webServer.tapIndex((html) => {
            if (typeof html !== 'string') return html
            if (html.indexOf(BRIDGE_PATH) !== -1) return html
            const tag = '<script defer src="' + BRIDGE_PATH + '"></script>'
            return html.indexOf('</body>') !== -1 ? html.replace('</body>', tag + '</body>') : html + tag
          }))
        }
      } catch (err) {}

      // —— 桌面伴侣 ——
      const companion = findCompanion()
      let childStartedAt = 0
      let restartCount = 0
      let fastExitCount = 0

      const logInfo = (msg) => {
        try { if (ctx.logger && typeof ctx.logger.info === 'function') ctx.logger.info('[dsh-pet] ' + msg) } catch (err) {}
      }
      const logWarn = (msg) => {
        try { if (ctx.logger && typeof ctx.logger.warn === 'function') ctx.logger.warn('[dsh-pet] ' + msg) } catch (err) {}
      }

      // 用户主动退出标记（契约：伴侣在托盘「退出」时写，启动时删；插件在 DSH 启动时也删）
      const quitFlagExists = () => {
        try {
          if (!fs.existsSync(QUIT_FLAG)) return false
          let reason = 'user'
          try {
            const parsed = JSON.parse(fs.readFileSync(QUIT_FLAG, 'utf8'))
            if (parsed && typeof parsed.reason === 'string') reason = parsed.reason
          } catch (err) {}
          // reason 不是 user 的（关机/注销等其它退出）一律当标记不存在
          return reason === 'user'
        } catch (err) {
          return false
        }
      }
      const clearQuitFlag = (why) => {
        try {
          if (fs.existsSync(QUIT_FLAG)) {
            fs.unlinkSync(QUIT_FLAG)
            logInfo('清除 quit.flag（' + why + '）')
          }
        } catch (err) {}
      }

      // 已被别的实例占着？（单实例互斥量会让后来者立刻 exit 0）
      const companionRunningElsewhere = () =>
        new Promise((resolve) => {
          try {
            const probe = spawn('tasklist', ['/FI', 'IMAGENAME eq DshPetFloat.exe', '/NH'], {
              windowsHide: true,
              stdio: ['ignore', 'pipe', 'ignore'],
            })
            let out = ''
            probe.stdout.on('data', (d) => { out += String(d) })
            probe.on('error', () => resolve(false))
            probe.on('close', () => resolve(/DshPetFloat\.exe/i.test(out)))
          } catch (err) {
            resolve(false)
          }
        })

      const scheduleRestart = () => {
        if (disposed) return
        if (quitFlagExists()) {
          logInfo('用户已手动退出伴侣（quit.flag 在），不自动拉起')
          return
        }
        if (fastExitCount >= 3) {
          logWarn('伴侣连续秒退（可能已有另一个实例在跑），不再自动拉起')
          return
        }
        if (restartCount >= 5) {
          logWarn('伴侣反复退出，已停止自动拉起；重启 DSH 可恢复')
          return
        }
        restartCount += 1
        const delay = Math.min(8000, 400 * restartCount * restartCount)
        const timer = setTimeout(() => {
          if (!disposed && !quitFlagExists()) launchCompanion()
        }, delay)
        if (timer && typeof timer.unref === 'function') timer.unref()
      }

      function launchCompanion() {
        if (!companion || disposed) return
        try {
          const child = spawn(companion, ['--state', STATE_FILE, '--cmd', CMD_FILE], {
            detached: true,
            stdio: 'ignore',
            windowsHide: false,
          })
          spawnedChild = child
          childStartedAt = Date.now()
          child.on('error', () => {})
          child.on('exit', (code) => {
            if (spawnedChild === child) spawnedChild = null
            if (disposed) return
            const alive = Date.now() - childStartedAt
            // 活得够久＝正常使用过，重启计数清零
            if (alive > 30000) { restartCount = 0; fastExitCount = 0 }
            // 2 秒内 exit 0：多半是单实例互斥量把后来者踢出来了，先确认是不是已有实例在跑
            if (alive < 2000 && (code === 0 || code === null)) {
              companionRunningElsewhere().then((running) => {
                if (disposed) return
                if (running) {
                  logInfo('伴侣已在运行，忽略这次启动即退出')
                  return
                }
                fastExitCount += 1
                scheduleRestart()
              })
              return
            }
            if (alive < 3000) fastExitCount += 1
            scheduleRestart()
          })
          child.unref()
        } catch (err) {
          logWarn('伴侣启动失败：' + String((err && err.message) || err))
        }
      }

      // 先落一版状态，伴侣起来就能立刻看到内容
      flush(true)
      // 余额：启动后马上拉一次（不阻塞），之后每分钟一次
      scheduleWalletRefresh(1500)
      const walletTick = setInterval(() => {
        if (disposed) return
        scheduleWalletRefresh(0)
      }, WALLET_INTERVAL_MS)
      if (walletTick && typeof walletTick.unref === 'function') walletTick.unref()
      disposers.push(() => clearInterval(walletTick))
      // DSH 启动 = 用户想要它，清掉上次手动退出的标记
      clearQuitFlag('DSH 启动')
      launchCompanion()

      const heartbeat = setInterval(() => {
        if (disposed) return
        try {
          flush(true)
        } catch (err) {}
      }, 1000)
      if (heartbeat && typeof heartbeat.unref === 'function') heartbeat.unref()
      disposers.push(() => clearInterval(heartbeat))
      disposers.push(() => {
        try {
          if (spawnedChild && spawnedChild.pid) spawnedChild.kill()
        } catch (err) {}
      })
    })
  },
}
