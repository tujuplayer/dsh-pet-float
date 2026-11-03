// 钱包账本测试：纯函数 + 跟踪器（假时钟、假余额来源）
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  beijingDay,
  beijingDayStart,
  formatMoney,
  formatTokens,
  addTokens,
  applyBalance,
  applySeed,
  parseWhaleSeed,
  createWalletTracker,
  rollDay,
} from '../lib/wallet.mjs'

function tempDir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dshp-wallet-'))
  return dir
}

function at(day, hh = 0, mm = 0) {
  // day = '2026-11-04'，按北京时间构造毫秒
  const [y, m, d] = day.split('-').map(Number)
  return Date.UTC(y, m - 1, d, hh, mm) - 8 * 3600 * 1000
}

test('beijingDay / beijingDayStart 按 +08:00 分日', () => {
  assert.equal(beijingDay(at('2026-11-04', 0, 0)), '2026-11-04')
  assert.equal(beijingDay(at('2026-11-04', 23, 59)), '2026-11-04')
  // UTC 15:59 = 北京 23:59，仍属当天
  assert.equal(beijingDay(Date.UTC(2026, 10, 4, 15, 59)), '2026-11-04')
  // UTC 16:00 = 北京次日 00:00
  assert.equal(beijingDay(Date.UTC(2026, 10, 4, 16, 0)), '2026-11-05')
  assert.equal(beijingDayStart(at('2026-11-04', 12, 0)), at('2026-11-04', 0, 0))
})

test('formatMoney / formatTokens', () => {
  assert.equal(formatMoney(77.718188), '77.72')
  assert.equal(formatMoney(0), '0.00')
  assert.equal(formatMoney(null), '')
  assert.equal(formatTokens(0), '0')
  assert.equal(formatTokens(1234), '1234')
  assert.equal(formatTokens(152340), '15.2万')
  assert.equal(formatTokens(1234000), '123万')
})

test('首次观测当天：dayStart=余额，partial 看首次观测是否在 00:10 之后', () => {
  const store = { date: '2026-11-04', dayStart: null, lastBalance: null, lastBalanceAt: 0, todaySpend: 0, todayTokens: 0, partial: false, history: {} }
  applyBalance(store, 100, at('2026-11-04', 0, 3))
  assert.equal(store.dayStart, 100)
  assert.equal(store.todaySpend, 0)
  assert.equal(store.partial, false, '00:03 观测 ⇒ 不 partial')

  const late = { date: '2026-11-04', dayStart: null, lastBalance: null, lastBalanceAt: 0, todaySpend: 0, todayTokens: 0, partial: false, history: {} }
  applyBalance(late, 100, at('2026-11-04', 9, 0))
  assert.equal(late.partial, true, '09:00 才观测 ⇒ partial')
})

test('余额下降 = 今日花费', () => {
  const store = { date: '2026-11-04', dayStart: null, lastBalance: null, lastBalanceAt: 0, todaySpend: 0, todayTokens: 0, partial: false, history: {} }
  applyBalance(store, 100, at('2026-11-04', 0, 1))
  applyBalance(store, 99.2, at('2026-11-04', 1, 0))
  assert.equal(store.todaySpend, 0.8)
  applyBalance(store, 98.75, at('2026-11-04', 2, 0))
  assert.equal(store.todaySpend, 1.25)
})

test('充值不会变成负花费，基准同步上移', () => {
  const store = { date: '2026-11-04', dayStart: null, lastBalance: null, lastBalanceAt: 0, todaySpend: 0, todayTokens: 0, partial: false, history: {} }
  applyBalance(store, 10, at('2026-11-04', 0, 1))
  applyBalance(store, 9, at('2026-11-04', 1, 0))
  assert.equal(store.todaySpend, 1)
  // 充值 100 → 余额 109，花费仍应是 1 而不是 0 或负数
  applyBalance(store, 109, at('2026-11-04', 2, 0))
  assert.equal(store.todaySpend, 1)
  assert.equal(store.dayStart, 110)
  // 再花 0.5 → 花费 1.5
  applyBalance(store, 108.5, at('2026-11-04', 3, 0))
  assert.equal(store.todaySpend, 1.5)
})

test('跨天滚动：旧的一天进 history，基准与 token 清零', () => {
  const store = { date: '2026-11-04', dayStart: 100, lastBalance: 98, lastBalanceAt: at('2026-11-04', 20), todaySpend: 2, todayTokens: 999, partial: false, history: {} }
  addTokens(store, 100, at('2026-11-04', 21))
  assert.equal(store.todayTokens, 1099)
  rollDay(store, at('2026-11-05', 0, 5))
  assert.equal(store.date, '2026-11-05')
  assert.equal(store.dayStart, null)
  assert.equal(store.todaySpend, 0)
  assert.equal(store.todayTokens, 0)
  assert.equal(store.history['2026-11-04'], 2)
})

test('跟踪器：刷新成功 → ok:true 并按 SPEC 字段输出', async () => {
  const dir = tempDir()
  const file = path.join(dir, '.dshp-wallet.json')
  let clock = at('2026-11-04', 0, 2)
  let balance = 77.834642
  const tracker = createWalletTracker({
    file,
    now: () => clock,
    readBalance: async () => ({ ok: true, balance, currency: 'CNY', source: 'account' }),
  })
  let view = await tracker.refresh()
  assert.equal(view.ok, true)
  assert.equal(view.source, 'account')
  assert.equal(view.balance, 77.834642)
  assert.equal(view.dayStart, 77.834642)
  assert.equal(view.todaySpend, 0)
  assert.equal(view.partial, false)
  assert.ok(view.updatedAt > 0)

  clock = at('2026-11-04', 1, 0)
  balance = 77.718188
  view = await tracker.refresh()
  assert.equal(view.todaySpend, 0.116454)

  // 落盘后重新构造跟踪器：当天基准不能丢
  const again = createWalletTracker({ file, now: () => clock, readBalance: async () => ({ ok: false, error: 'offline' }) })
  await again.refresh()
  const kept = again.debugStore()
  assert.equal(kept.date, '2026-11-04')
  assert.equal(kept.dayStart, 77.834642)
  assert.equal(kept.lastBalance, 77.718188)
})

test('跟踪器：刷新失败保留上次数字但 ok:false + error', async () => {
  const dir = tempDir()
  const file = path.join(dir, 'w.json')
  const clock = at('2026-11-04', 0, 2)
  let fail = false
  const tracker = createWalletTracker({
    file,
    now: () => clock,
    readBalance: async () => (fail ? { ok: false, error: '未登录 DeepSeek 账号' } : { ok: true, balance: 50, source: 'api-key' }),
  })
  await tracker.refresh()
  fail = true
  const view = await tracker.refresh()
  assert.equal(view.ok, false)
  assert.equal(view.error, '未登录 DeepSeek 账号')
  // 数字仍是上次的（伴侣能继续显示，只是标记未知）
  assert.equal(view.balance, null)
  assert.equal(view.dayStart, 50)
})

test('跟踪器：token 累加与跨天重置', async () => {
  const dir = tempDir()
  const file = path.join(dir, 'w.json')
  let clock = at('2026-11-04', 0, 2)
  const tracker = createWalletTracker({ file, now: () => clock, readBalance: async () => ({ ok: true, balance: 10 }) })
  await tracker.refresh()
  tracker.addTurnTokens(1200)
  tracker.addTurnTokens(800)
  assert.equal(tracker.current().todayTokens, 2000)
  clock = at('2026-11-05', 0, 1)
  tracker.addTurnTokens(500)
  assert.equal(tracker.current().todayTokens, 500)
})

test('跟踪器：并发 refresh 只打一次余额接口', async () => {
  const dir = tempDir()
  const file = path.join(dir, 'w.json')
  let calls = 0
  const tracker = createWalletTracker({
    file,
    now: () => at('2026-11-04', 10, 0),
    readBalance: async () => {
      calls += 1
      await new Promise((r) => setTimeout(r, 20))
      return { ok: true, balance: 42 }
    },
  })
  const [a, b, c] = await Promise.all([tracker.refresh(), tracker.refresh(), tracker.refresh()])
  assert.equal(calls, 1)
  assert.equal(a.balance, 42)
  assert.equal(b.balance, 42)
  assert.equal(c.balance, 42)
})

// —— v1.2.0：从鲸鱼挂件账本接上「今日花费」的当天基准 ——

test('鲸鱼种子：只认当天，取 dayStart 与当天事件 token 之和', () => {
  const now = at('2026-11-04', 12, 0)
  const raw = {
    date: '2026-11-04',
    dayStart: 77.834642,
    lastBalance: 74.59078,
    events: [
      { ts: 1, day: '2026-11-03', tokens: 999, cost: 1 },
      { ts: 2, day: '2026-11-04', tokens: 121651, cost: 0.5 },
      { ts: 3, day: '2026-11-04', tokens: 80000, cost: 0.2 },
    ],
  }
  const seed = parseWhaleSeed(raw, now)
  assert.equal(seed.dayStart, 77.834642)
  // 不采纳鲸鱼的 token（口径不同）
  assert.equal(seed.tokens, 0)
  // 昨天/坏数据都不认
  assert.equal(parseWhaleSeed({ ...raw, date: '2026-11-03' }, now), null)
  assert.equal(parseWhaleSeed(null, now), null)
  assert.equal(parseWhaleSeed({ date: '2026-11-04', dayStart: 'x' }, now), null)
})

test('applySeed：接上基准后今日花费立刻正确，且 partial 取消', () => {
  const now = at('2026-11-04', 12, 0)
  const store = {
    version: 1,
    date: '2026-11-04',
    seededDay: '',
    dayStart: 74.59078,
    lastBalance: 74.59078,
    lastBalanceAt: now,
    todaySpend: 0,
    todayTokens: 0,
    partial: true,
    currency: 'CNY',
    ok: true,
    source: 'account',
    error: '',
    updatedAt: now,
    history: {},
  }
  const changed = applySeed(store, { date: '2026-11-04', dayStart: 77.834642, tokens: 201651 }, now)
  assert.equal(changed, true)
  assert.equal(store.dayStart, 77.834642)
  assert.equal(store.todaySpend, 3.243862)
  assert.equal(store.todayTokens, 0)
  assert.equal(store.partial, false)
  assert.equal(store.seededDay, '2026-11-04')
  // 同一天不重复接（不会把后来的充值基准盖回去）
  assert.equal(applySeed(store, { date: '2026-11-04', dayStart: 99, tokens: 1 }, now), false)
  assert.equal(store.dayStart, 77.834642)
})

test('跟踪器：有鲸鱼种子时首次刷新就能算出真实今日花费', async () => {
  const dir = tempDir()
  const file = path.join(dir, 'w.json')
  const now = at('2026-11-04', 12, 0)
  const tracker = createWalletTracker({
    file,
    now: () => now,
    readBalance: async () => ({ ok: true, balance: 74.59078 }),
    seedSource: () => ({
      date: '2026-11-04',
      dayStart: 77.834642,
      events: [{ day: '2026-11-04', tokens: 201651 }],
    }),
  })
  const view = await tracker.refresh()
  assert.equal(view.ok, true)
  assert.equal(view.todaySpend, 3.243862)
  assert.equal(view.partial, false)
  // 落盘后重载不应丢掉基准
  const again = createWalletTracker({
    file,
    now: () => now,
    readBalance: async () => ({ ok: true, balance: 74.5 }),
    seedSource: () => ({ date: '2026-11-04', dayStart: 77.834642, events: [] }),
  })
  const second = await again.refresh()
  assert.ok(Math.abs(second.todaySpend - 3.334642) < 1e-6)
})

test('跟踪器：种子日期不是今天就不采纳', async () => {
  const dir = tempDir()
  const file = path.join(dir, 'w.json')
  const tracker = createWalletTracker({
    file,
    now: () => at('2026-11-04', 12, 0),
    readBalance: async () => ({ ok: true, balance: 50 }),
    seedSource: () => ({ date: '2026-11-03', dayStart: 77.8, events: [] }),
  })
  const view = await tracker.refresh()
  assert.equal(view.todaySpend, 0)
  assert.equal(view.partial, true)
})
