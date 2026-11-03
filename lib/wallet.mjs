// 钱包跟踪：余额（账号登录态或 API key）+ 今日花费（余额差分日账本）+ 今日 token。
//
// 设计要点（对应 SPEC 3.5.1）：
// - 余额只信任「宿主自己取到的数」；取不到就 ok:false，绝不猜。
// - 今日花费不靠价格表估算，而是「当天首次看到的余额 - 当前余额」，充值会把基准上移（不把充值算成负花费）。
// - 当天不是从 00:00 就开始观测的（> 10 分钟后才有第一次观测）→ partial=true，界面显示「≥」。
// - 账本落盘在 $DSH_HOME/.dshp-wallet.json，保留最近 7 天 history，DSH 重启不丢当天基准。

import fs from 'node:fs'
import path from 'node:path'

export const WALLET_VERSION = 1
export const LATE_OBSERVE_MS = 10 * 60 * 1000
export const HISTORY_DAYS = 7

const BEIJING_OFFSET_MS = 8 * 60 * 60 * 1000

/** 北京时间的 YYYY-MM-DD（与鲸鱼插件的分日口径一致，SPEC/记账都按 +08:00 分天） */
export function beijingDay(ms) {
  const d = new Date(Number(ms) + BEIJING_OFFSET_MS)
  const y = d.getUTCFullYear()
  const m = String(d.getUTCMonth() + 1).padStart(2, '0')
  const day = String(d.getUTCDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

/** 北京时间的当天 00:00 对应的毫秒时间戳 */
export function beijingDayStart(ms) {
  const t = Number(ms) + BEIJING_OFFSET_MS
  const dayStart = Math.floor(t / 86400000) * 86400000
  return dayStart - BEIJING_OFFSET_MS
}

export function round6(n) {
  const v = Number(n)
  if (!Number.isFinite(v)) return null
  return Math.round(v * 1e6) / 1e6
}

/** 金额显示：¥77.72；未知返回 '' */
export function formatMoney(value) {
  if (value === null || value === undefined || value === '') return ''
  const v = Number(value)
  if (!Number.isFinite(v)) return ''
  return v.toFixed(2)
}

/** token 短写：152340 → 15.2万 */
export function formatTokens(value) {
  const v = Number(value)
  if (!Number.isFinite(v) || v <= 0) return '0'
  if (v < 10000) return String(Math.round(v))
  const wan = v / 10000
  return `${wan >= 100 ? Math.round(wan) : wan.toFixed(1)}万`
}

function emptyStore(now) {
  return {
    version: WALLET_VERSION,
    date: beijingDay(now),
    dayStart: null,
    lastBalance: null,
    lastBalanceAt: 0,
    todaySpend: 0,
    todayTokens: 0,
    partial: false,
    currency: 'CNY',
    ok: false,
    source: 'none',
    error: '',
    updatedAt: 0,
    history: {},
  }
}

function normalizeStore(raw, now) {
  const base = emptyStore(now)
  if (!raw || typeof raw !== 'object') return base
  const num = (v) => {
    const n = Number(v)
    return Number.isFinite(n) ? n : null
  }
  return {
    version: WALLET_VERSION,
    date: typeof raw.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(raw.date) ? raw.date : base.date,
    dayStart: num(raw.dayStart),
    lastBalance: num(raw.lastBalance),
    lastBalanceAt: num(raw.lastBalanceAt) || 0,
    todaySpend: num(raw.todaySpend) || 0,
    todayTokens: num(raw.todayTokens) || 0,
    partial: !!raw.partial,
    currency: typeof raw.currency === 'string' && raw.currency ? raw.currency : 'CNY',
    ok: !!raw.ok,
    source: typeof raw.source === 'string' ? raw.source : 'none',
    error: typeof raw.error === 'string' ? raw.error : '',
    updatedAt: num(raw.updatedAt) || 0,
    history: raw.history && typeof raw.history === 'object' ? { ...raw.history } : {},
  }
}

/** 分日滚动：换天就把前一天的花费记进 history，基准重置为当前余额 */
export function rollDay(store, now) {
  const today = beijingDay(now)
  if (store.date === today) return store
  // 跨天：把旧的一天的花费归档（只保留最近 HISTORY_DAYS 天）
  if (store.date && store.dayStart !== null) {
    store.history[store.date] = round6(store.todaySpend) || 0
  }
  const keys = Object.keys(store.history).sort()
  while (keys.length > HISTORY_DAYS) {
    const drop = keys.shift()
    if (drop) delete store.history[drop]
  }
  store.date = today
  store.dayStart = null
  store.lastBalance = null
  store.lastBalanceAt = 0
  store.todaySpend = 0
  store.todayTokens = 0
  store.partial = false
  return store
}

/**
 * 用一次成功的余额读数更新账本。
 * 返回 { changed, toppedUp }，方便调用方与测试断言。
 */
export function applyBalance(store, balance, now) {
  rollDay(store, now)
  const value = round6(balance)
  if (value === null) return { changed: false, toppedUp: false }
  let changed = false
  if (store.dayStart === null) {
    store.dayStart = value
    // 当天不是从头观测的 → 今日花费只是「部分数据」
    store.partial = Number(now) - beijingDayStart(now) > LATE_OBSERVE_MS
    changed = true
  } else if (store.lastBalance !== null && value > store.lastBalance + 0.005) {
    // 充值：把基准一起上移，避免出现「负花费」
    store.dayStart = round6(store.dayStart + (value - store.lastBalance))
    changed = true
  }
  const spend = Math.max(0, round6((store.dayStart ?? value) - value) || 0)
  if (spend !== store.todaySpend) {
    store.todaySpend = spend
    changed = true
  }
  if (store.lastBalance !== value) {
    store.lastBalance = value
    changed = true
  }
  store.lastBalanceAt = Number(now)
  return { changed, toppedUp: false }
}

/** 每日 token 累加（turn/end 时喂进来） */
export function addTokens(store, tokens, now) {
  rollDay(store, now)
  const n = Number(tokens)
  if (!Number.isFinite(n) || n <= 0) return store
  store.todayTokens = Math.round((store.todayTokens || 0) + n)
  return store
}

/**
 * 创建钱包跟踪器。
 * options:
 *   file     —— 账本文件路径（默认 %DSH_HOME%/.dshp-wallet.json）
 *   readBalance —— async () => { ok, balance, currency, source, error }
 *   now      —— () => ms，测试用
 *   log      —— (level, msg) => void
 */
export function createWalletTracker(options = {}) {
  const file = options.file || path.join(process.env.DSH_HOME || '', '.dshp-wallet.json')
  const readBalance = typeof options.readBalance === 'function' ? options.readBalance : async () => ({ ok: false, error: '未配置余额来源' })
  const now = typeof options.now === 'function' ? options.now : () => Date.now()
  const log = typeof options.log === 'function' ? options.log : () => {}

  let store = load()
  let inflight = null
  let lastLoadError = ''

  function load() {
    const base = emptyStore(now())
    try {
      const raw = JSON.parse(fs.readFileSync(file, 'utf8'))
      return normalizeStore(raw, now())
    } catch (err) {
      if (err && err.code !== 'ENOENT') lastLoadError = String(err.message || err)
      return base
    }
  }

  function persist() {
    try {
      const dir = path.dirname(file)
      fs.mkdirSync(dir, { recursive: true })
      const tmp = `${file}.tmp`
      fs.writeFileSync(tmp, JSON.stringify(store))
      fs.renameSync(tmp, file)
    } catch (err) {
      log('warn', '钱包账本写盘失败：' + String((err && err.message) || err))
    }
  }

  /** 拉一次余额（并发去重；失败不清空已有数字，只标 ok:false） */
  async function refresh() {
    if (inflight) return inflight
    inflight = (async () => {
      const at = now()
      let result
      try {
        result = await readBalance()
      } catch (err) {
        result = { ok: false, error: String((err && err.message) || err).slice(0, 120) }
      }
      rollDay(store, at)
      if (result && result.ok && Number.isFinite(Number(result.balance))) {
        const before = JSON.stringify(store)
        applyBalance(store, Number(result.balance), at)
        store.ok = true
        store.error = ''
        store.source = result.source || 'account'
        if (result.currency) store.currency = String(result.currency)
        store.updatedAt = at
        if (JSON.stringify(store) !== before) persist()
      } else {
        const message = String((result && result.error) || '取不到余额').slice(0, 120)
        if (store.ok || store.error !== message) {
          store.ok = false
          store.error = message
          store.updatedAt = at
          persist()
        }
      }
      return current()
    })()
    try {
      return await inflight
    } finally {
      inflight = null
    }
  }

  /** 供 engine.snapshot 用的只读视图（严格按 SPEC 3.5.1 的字段名） */
  function current() {
    return {
      ok: !!store.ok,
      source: store.source || 'none',
      currency: store.currency || 'CNY',
      balance: store.ok && store.lastBalance !== null ? round6(store.lastBalance) : null,
      dayStart: store.dayStart === null ? null : round6(store.dayStart),
      todaySpend: round6(store.todaySpend) || 0,
      todayTokens: Math.round(store.todayTokens || 0),
      partial: !!store.partial,
      updatedAt: store.updatedAt || 0,
      error: store.ok ? '' : store.error || '',
    }
  }

  function addTurnTokens(tokens) {
    const at = now()
    rollDay(store, at)
    addTokens(store, tokens, at)
    persist()
  }

  function reset(keepHistory = true) {
    const history = keepHistory ? store.history : {}
    store = emptyStore(now())
    store.history = history
    persist()
  }

  return {
    file,
    refresh,
    current,
    addTurnTokens,
    reset,
    /** 测试/调试用：直接读内部账本副本 */
    debugStore: () => JSON.parse(JSON.stringify(store)),
    lastLoadError: () => lastLoadError,
  }
}
