/**
 * 宿主插件胶水层测试：用一个假 cordis ctx + 真 HTTP server 跑 lib/index.js
 *
 * 验证（不碰真 DSH）：
 *   · 插件能加载、注册路由、注册桌面端注入行
 *   · session/event → 状态文件按 SPEC 落盘（含 waiting-question 事件）
 *   · 命令文件 → /dsh-pet/page 带回来 → /dsh-pet/cmd-ack 删除
 *   · 信任栅栏：非回环 Host / 跨站 Origin / Sec-Fetch-Site: cross-site 一律 403
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import fs from 'node:fs'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'

const TEMP_HOME = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-pet-test-'))
process.env.DSH_HOME = TEMP_HOME
process.env.DSH_PET_EXE = ''
process.env.DSH_PET_QUIT_FLAG = path.join(TEMP_HOME, 'quit.flag')

const { default: plugin } = await import('../lib/index.js')

const STATE_FILE = path.join(TEMP_HOME, '.dshp-state.json')
const CMD_FILE = path.join(TEMP_HOME, '.dshp-cmd.json')

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
const waitFor = async (predicate, timeout = 4000) => {
  const deadline = Date.now() + timeout
  while (Date.now() < deadline) {
    if (await predicate()) return true
    await sleep(50)
  }
  return false
}

/** 假 cordis：register 收集路由，on 收集事件，effect 收集清理器 */
function makeHost() {
  const routes = new Map()
  const listeners = new Map()
  const cleanups = []
  const services = new Map()
  let server = null
  let port = 0

  const webServer = {
    get port() { return port },
    register(route) {
      routes.set(route.path, route.handler)
      return () => routes.delete(route.path)
    },
    tapIndex(fn) {
      return () => {}
    },
  }

  const ctx = {
    get: (name) => services.get(name) || null,
    on: (name, fn) => {
      if (!listeners.has(name)) listeners.set(name, [])
      listeners.get(name).push(fn)
      return () => {
        const list = listeners.get(name) || []
        const index = list.indexOf(fn)
        if (index >= 0) list.splice(index, 1)
      }
    },
    effect: (fn) => {
      const disposer = fn()
      if (typeof disposer === 'function') cleanups.push(disposer)
      return () => {}
    },
    inject: (deps, cb) => {
      cb(ctx)
    },
    logger: { warn: () => {}, info: () => {}, error: () => {} },
    webServer,
  }

  const emit = (name, ...args) => {
    for (const fn of listeners.get(name) || []) fn(...args)
  }

  return {
    ctx,
    webServer,
    routes,
    listeners,
    services,
    emit,
    async start() {
      server = http.createServer((req, res) => {
        const pathname = new URL(req.url, 'http://127.0.0.1').pathname
        const handler = routes.get(pathname)
        if (!handler) {
          res.writeHead(404, { 'Content-Type': 'text/plain' })
          res.end('no route')
          return
        }
        Promise.resolve(handler(req, res)).catch((err) => {
          try {
            res.writeHead(500, { 'Content-Type': 'text/plain' })
            res.end('handler error: ' + String((err && err.message) || err))
          } catch (err2) {}
        })
      })
      await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
      port = server.address().port
      return port
    },
    async stop() {
      if (server) await new Promise((resolve) => server.close(resolve))
      server = null
      // 必须跑插件的清理器：否则上一个 host 的心跳/钱包定时器会继续往同一个
      // STATE_FILE 写旧快照，把下一个用例刚写的内容盖掉（曾让测试 5 偶发失败）。
      for (const cleanup of cleanups.splice(0)) {
        try { cleanup() } catch (err) {}
      }
    },
  }
}

function request(port, options = {}) {
  const { method = 'GET', path: urlPath = '/', headers = {}, body = null } = options
  return new Promise((resolve) => {
    const req = http.request({ host: '127.0.0.1', port, method, path: urlPath, headers }, (res) => {
      const chunks = []
      res.on('data', (chunk) => chunks.push(chunk))
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8')
        let json = null
        try { json = JSON.parse(text) } catch (err) {}
        resolve({ status: res.statusCode, headers: res.headers, text, json })
      })
    })
    req.on('error', (err) => resolve({ status: 0, error: String(err && err.message), text: '', json: null }))
    if (body) req.write(typeof body === 'string' ? body : JSON.stringify(body))
    req.end()
  })
}

const readState = () => {
  try {
    return JSON.parse(fs.readFileSync(STATE_FILE, 'utf8'))
  } catch (err) {
    return null
  }
}

test('插件加载后注册路由 / 注入行，并落一版状态文件', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    assert.ok(fs.existsSync(STATE_FILE), '初始状态文件应写出来')
    const state = readState()
    assert.equal(state.schema, 1)
    assert.equal(state.host.port, 0)
    assert.ok(state.seq >= 1)
    assert.ok(state.ts > 0)

    for (const route of ['/dsh-pet/ping', '/dsh-pet/state.json', '/dsh-pet/page', '/dsh-pet/cmd-ack', '/dsh-pet/pet-bridge.js']) {
      assert.ok(host.routes.has(route), '应注册路由 ' + route)
    }

    // 桌面端注入行
    const table = []
    host.emit('webserver/index-inject', table)
    assert.equal(table.length, 1)
    assert.equal(table[0].kind, 'script')
    assert.ok(table[0].text.indexOf('/dsh-pet/pet-bridge.js') >= 0)
    // 去重：再收集一次不能重复推
    host.emit('webserver/index-inject', table)
    assert.equal(table.length, 1)

    const ping = await request(host.webServer.port, { path: '/dsh-pet/ping' })
    assert.equal(ping.status, 200)
    assert.equal(ping.json.ok, true)

    const bridge = await request(host.webServer.port, { path: '/dsh-pet/pet-bridge.js' })
    assert.equal(bridge.status, 200)
    assert.ok(bridge.text.indexOf('__dshPetBridge') >= 0)
  } finally {
    await host.stop()
  }
})

test('会话事件驱动状态文件：提问挂起 → 解除', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    const session = { id: 'sess-1', name: '' }
    host.emit('session/event', session, {
      type: 'session/title',
      data: { title: '扩展宠物插件为悬浮球助手' },
    })
    host.emit('session/event', session, { type: 'turn/start', data: { turn: 3 } })
    await sleep(350)
    let state = readState()
    assert.equal(state.active.status, 'running')
    assert.equal(state.active.title, '扩展宠物插件为悬浮球助手')
    assert.equal(state.active.turn, 3)

    host.emit('session/event', session, {
      type: 'tool/call',
      data: {
        turn: 3,
        step: 5,
        callId: 'call-abc',
        name: 'ask_user_question',
        arguments: { questions: [{ id: 'q1', question: '先做插件还是先做悬浮球？', options: [{ label: '都要' }] }] },
      },
    })
    await waitFor(() => {
      const snap = readState()
      return snap && snap.active && snap.active.status === 'waiting-question'
    })
    state = readState()
    assert.equal(state.active.status, 'waiting-question')
    assert.equal(state.active.question.text, '先做插件还是先做悬浮球？')
    assert.deepEqual(state.active.question.options, ['都要'])
    assert.equal(state.counts.pending, 1)
    assert.ok(state.events.some((event) => event.kind === 'question'))

    host.emit('session/event', session, { type: 'tool/result', data: { turn: 3, step: 5, message: { callId: 'call-abc', content: [], isError: false } } })
    await waitFor(() => {
      const snap = readState()
      return snap && snap.active && snap.active.status === 'running'
    })
    assert.equal(readState().active.status, 'running')
  } finally {
    await host.stop()
  }
})

test('实时思考流经 agent/assistant-stream 进状态文件', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    const session = { id: 'sess-2', name: '' }
    host.emit('session/event', session, { type: 'turn/start', data: { turn: 1 } })
    host.emit('agent/assistant-stream', {
      agent: { session },
      frame: { type: 'chunk', chunk: { type: 'reasoning-delta', text: '先确认插件生命周期' } },
    })
    await waitFor(() => {
      const snap = readState()
      return snap && snap.active && snap.active.thinking === '先确认插件生命周期'
    })
    const state = readState()
    assert.equal(state.active.thinking, '先确认插件生命周期')
    assert.equal(state.active.thinkingSource, 'host')
  } finally {
    await host.stop()
  }
})

test('命令文件：/page 带回 → /cmd-ack 删除 → 再来为 null', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    const port = host.webServer.port
    const first = await request(port, { method: 'POST', path: '/dsh-pet/page', headers: { 'Content-Type': 'application/json' }, body: { title: 'SanXiao', visible: true } })
    assert.equal(first.status, 200)
    assert.equal(first.json.cmd, null)

    fs.writeFileSync(CMD_FILE, JSON.stringify({ id: 5, action: 'open', title: 'SanXiao', ts: Date.now() }))
    const second = await request(port, { method: 'POST', path: '/dsh-pet/page', headers: { 'Content-Type': 'application/json' }, body: { title: 'SanXiao', visible: true } })
    assert.equal(second.json.cmd.id, 5)
    assert.equal(second.json.cmd.title, 'SanXiao')
    assert.equal(second.json.cmd.action, 'open')

    // 同一条命令不重复下发（页面若没 ack，说明它已经拿到了）
    const third = await request(port, { method: 'POST', path: '/dsh-pet/page', headers: { 'Content-Type': 'application/json' }, body: {} })
    assert.equal(third.json.cmd, null)

    const ack = await request(port, { method: 'POST', path: '/dsh-pet/cmd-ack', headers: { 'Content-Type': 'application/json' }, body: { id: 5 } })
    assert.equal(ack.status, 200)
    assert.equal(fs.existsSync(CMD_FILE), false, 'ack 之后命令文件应删除')
  } finally {
    await host.stop()
  }
})

test('页面心跳让 page.alive=true 且带上标题', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    const port = host.webServer.port
    await request(port, {
      method: 'POST',
      path: '/dsh-pet/page',
      headers: { 'Content-Type': 'application/json' },
      body: { title: '页面标题', thinking: '页面读到的一行思考', visible: true, focused: false, href: 'http://127.0.0.1:8598/' },
    })
    await sleep(350)
    const state = readState()
    assert.equal(state.page.alive, true)
    assert.equal(state.page.title, '页面标题')
    assert.equal(state.page.visible, true)
    assert.equal(state.page.focused, false)
    assert.equal(state.active.title, '页面标题')
    assert.equal(state.active.thinking, '页面读到的一行思考')
    assert.equal(state.active.thinkingSource, 'page')
  } finally {
    await host.stop()
  }
})

test('信任栅栏：非回环 Host / 跨站 Origin / cross-site 一律拒绝', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    const port = host.webServer.port
    const badHost = await request(port, { path: '/dsh-pet/ping', headers: { Host: 'evil.example.com' } })
    assert.equal(badHost.status, 403)

    const badOrigin = await request(port, {
      path: '/dsh-pet/ping',
      headers: { Origin: 'http://evil.example.com' },
    })
    assert.equal(badOrigin.status, 403)

    const crossSite = await request(port, {
      path: '/dsh-pet/ping',
      headers: { 'Sec-Fetch-Site': 'cross-site' },
    })
    assert.equal(crossSite.status, 403)

    const good = await request(port, {
      path: '/dsh-pet/ping',
      headers: { Origin: 'http://127.0.0.1:' + port },
    })
    assert.equal(good.status, 200)

    const localhost = await request(port, {
      path: '/dsh-pet/ping',
      headers: { Origin: 'http://localhost:' + port, Host: 'localhost:' + port },
    })
    assert.equal(localhost.status, 200)
  } finally {
    await host.stop()
  }
})

test('状态文件是原子写（不会出现半截 JSON）', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    const session = { id: 'sess-3', name: '' }
    for (let i = 0; i < 20; i += 1) {
      host.emit('session/event', session, { type: 'turn/start', data: { turn: i + 1 } })
      const text = fs.readFileSync(STATE_FILE, 'utf8')
      assert.doesNotThrow(() => JSON.parse(text), '写盘途中读到的必须是完整 JSON')
    }
  } finally {
    await host.stop()
  }
})

test('DSH 启动时清掉上次的 quit.flag（用户又想让它出来了）', async () => {
  const flag = process.env.DSH_PET_QUIT_FLAG
  fs.mkdirSync(path.dirname(flag), { recursive: true })
  fs.writeFileSync(flag, JSON.stringify({ ts: Date.now(), reason: 'user' }))

  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    assert.equal(fs.existsSync(flag), false, 'apply 之后 quit.flag 应被清掉')
  } finally {
    await host.stop()
  }
})

test('没有伴侣 exe 时插件不报错也不写 quit.flag', async () => {
  const flag = process.env.DSH_PET_QUIT_FLAG
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    assert.equal(fs.existsSync(flag), false)
    const ping = await request(host.webServer.port, { path: '/dsh-pet/ping' })
    assert.equal(ping.status, 200)
  } finally {
    await host.stop()
  }
})

test('钱包：账号服务给余额 → 状态文件带 wallet，/dsh-pet/wallet.json 可读', async () => {
  const host = makeHost()
  // 假 deepseekAccount 服务：只有赠金（真实场景里新用户常见）
  host.services.set('deepseekAccount', {
    getState: () => ({ userId: 'user-1' }),
    getBalance: async () => ({
      status: 'ready',
      value: [],
      bonusWallets: [{ currency: 'CNY', balance: 12.5 }],
    }),
  })
  plugin.apply(host.ctx)
  await host.start()
  try {
    const port = host.webServer.port
    const wallet = await request(port, { method: 'POST', path: '/dsh-pet/wallet.json' })
    assert.equal(wallet.status, 200)
    assert.equal(wallet.json.ok, true)
    assert.equal(wallet.json.balance, 12.5)
    assert.equal(wallet.json.currency, 'CNY')
    assert.equal(wallet.json.source, 'account')

    // 状态文件里也要有 wallet（伴侣只读状态文件）
    await sleep(300)
    const state = readState()
    assert.equal(state.wallet.ok, true)
    assert.equal(state.wallet.balance, 12.5)
    assert.equal(typeof state.wallet.todaySpend, 'number')
    assert.equal(typeof state.wallet.todayTokens, 'number')
    assert.equal(state.wallet.partial, true, '测试在白天跑，属于"非当天从头观测"⇒ partial')
  } finally {
    await host.stop()
  }
})

test('钱包：没有账号服务也没有 API key → ok:false 但不影响状态文件其他字段', async () => {
  const host = makeHost()
  plugin.apply(host.ctx)
  await host.start()
  try {
    const port = host.webServer.port
    const wallet = await request(port, { method: 'POST', path: '/dsh-pet/wallet.json' })
    assert.equal(wallet.status, 200)
    assert.equal(wallet.json.ok, false)
    assert.ok(String(wallet.json.error || '').length > 0, '应带一行失败原因')
    await sleep(250)
    const state = readState()
    assert.equal(state.wallet.ok, false)
    assert.equal(state.active, null)
    assert.ok(state.host.petSoundFile !== undefined, 'host 里应有 petSoundFile 字段')
  } finally {
    await host.stop()
  }
})
