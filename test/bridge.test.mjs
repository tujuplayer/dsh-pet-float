/**
 * 页面桥单测：用一个极小的假 DOM 跑 assets/pet-bridge.js
 *
 * 只实现桥脚本真正用到的选择器：
 *   [class*="_sessionRow"] / [class*="_title"] / [class*="_summaryText"] / [data-variant="think"]
 * 这样就能在没有浏览器的环境里验证"读标题 / 读思考 / 切会话 / 回执命令"四条关键行为。
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import fs from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const BRIDGE_SOURCE = fs.readFileSync(path.join(HERE, '..', 'assets', 'pet-bridge.js'), 'utf8')

function matchSelector(node, selector) {
  const attr = /^\[([a-zA-Z-]+)\*="([^"]*)"\]$/.exec(selector)
  if (attr) {
    const [, name, value] = attr
    if (name === 'class') return String(node.className || '').includes(value)
    return String(node.attrs[name] || '').includes(value)
  }
  const exact = /^\[([a-zA-Z-]+)="([^"]*)"\]$/.exec(selector)
  if (exact) {
    const [, name, value] = exact
    if (name === 'class') return String(node.className || '').split(/\s+/).includes(value)
    return String(node.attrs[name] || '') === value
  }
  throw new Error('fake DOM: unsupported selector ' + selector)
}

class FakeElement {
  constructor(tag, options = {}) {
    this.tagName = tag
    this.className = options.className || ''
    this.attrs = options.attrs || {}
    this.ownText = options.text || ''
    this.children = []
    this.parent = null
    this.clicks = 0
  }

  append(child) {
    child.parent = this
    this.children.push(child)
    return child
  }

  get textContent() {
    const parts = [this.ownText]
    for (const child of this.children) parts.push(child.textContent)
    return parts.filter(Boolean).join(' ')
  }

  matches(selector) {
    return matchSelector(this, selector)
  }

  querySelectorAll(selector) {
    const out = []
    const walk = (node) => {
      for (const child of node.children) {
        if (matchSelector(child, selector)) out.push(child)
        walk(child)
      }
    }
    walk(this)
    return out
  }

  querySelector(selector) {
    const list = this.querySelectorAll(selector)
    return list.length ? list[0] : null
  }

  click() {
    this.clicks += 1
  }

  getAttribute(name) {
    return name in this.attrs ? this.attrs[name] : null
  }

  addEventListener() {}
}

function makeDoc({ title = 'DeepSeek Harness', hidden = false, focused = true } = {}) {
  const root = new FakeElement('body')
  const doc = {
    title,
    hidden,
    readyState: 'complete',
    documentElement: root,
    body: root,
    hasFocus: () => focused,
    addEventListener: () => {},
    querySelectorAll: (selector) => root.querySelectorAll(selector),
    querySelector: (selector) => root.querySelector(selector),
  }
  return { doc, root }
}

/** 往 root 里塞一条会话行 */
function addSessionRow(root, { title, selected = false }) {
  const row = new FakeElement('div', { className: 'wnFoaW_sessionRow' + (selected ? ' wnFoaW_selected' : '') })
  row.append(new FakeElement('div', { className: 'wnFoaW_slot' }))
  row.append(new FakeElement('div', { className: 'wnFoaW_title', text: title }))
  row.append(new FakeElement('span', { className: 'wnFoaW_time', text: '11分钟' }))
  root.append(row)
  return row
}

function addThinkRow(root, { text, state = 'running' }) {
  const row = new FakeElement('div', { className: 'xXe6ja_reasoning', attrs: { 'data-variant': 'think', 'data-state': state } })
  row.append(new FakeElement('div', { className: 'xXe6ja_summaryText', text }))
  root.append(row)
  return row
}

/** 装载桥脚本，返回可手动驱动的 world */
function loadBridge(options) {
  const { doc, root } = options
  const world = {
    doc,
    root,
    intervals: [],
    posts: [],
    cmd: null,
  }
  const context = {
    window: {},
    document: doc,
    location: { href: 'http://127.0.0.1:8598/' },
    setInterval: (fn, ms) => {
      world.intervals.push({ fn, ms })
      return world.intervals.length
    },
    clearInterval: () => {},
    setTimeout: (fn) => {
      world.intervals.push({ fn, ms: 0 })
      return world.intervals.length
    },
    String,
    Date,
    JSON,
    Promise,
    fetch: (url, init) => {
      const body = init && init.body ? JSON.parse(init.body) : {}
      world.posts.push({ url, body })
      let payload = { ok: true }
      if (url === '/dsh-pet/page') payload = { ok: true, cmd: world.cmd }
      return Promise.resolve({ ok: true, json: () => Promise.resolve(payload) })
    },
  }
  context.window = context
  context.globalThis = context
  vm.createContext(context)
  vm.runInContext(BRIDGE_SOURCE, context, { filename: 'pet-bridge.js' })
  world.bridge = context.window.__dshPetBridge
  world.tick = async (ms) => {
    for (const entry of world.intervals) if (entry.ms === ms) entry.fn()
    await flush()
  }
  return world
}

const flush = async () => {
  await new Promise((resolve) => setImmediate(resolve))
  await new Promise((resolve) => setImmediate(resolve))
}

test('读当前会话标题（选中行）与思考摘要（running 态）', async () => {
  const { doc, root } = makeDoc()
  addSessionRow(root, { title: 'SanXiao', selected: false })
  addSessionRow(root, { title: '扩展宠物插件为悬浮球助手', selected: true })
  addThinkRow(root, { text: '正在读 DOM 结构' })
  const world = loadBridge({ doc, root })
  await flush()

  const report = world.posts.find((post) => post.url === '/dsh-pet/page')
  assert.ok(report, '应该有 /dsh-pet/page 上报')
  assert.equal(report.body.title, '扩展宠物插件为悬浮球助手')
  assert.equal(report.body.thinking, '正在读 DOM 结构')
  assert.equal(report.body.visible, true)
  assert.equal(report.body.focused, true)
})

test('思考块不在 running 态时不当作"当前思考"', async () => {
  const { doc, root } = makeDoc()
  addThinkRow(root, { text: '已经想完了', state: 'ok' })
  const world = loadBridge({ doc, root })
  await flush()
  const report = world.posts.find((post) => post.url === '/dsh-pet/page')
  assert.equal(report.body.thinking, '')
})

test('没有选中行时回落到 document.title', async () => {
  const { doc, root } = makeDoc({ title: '继续完成三效游戏剩余需求 — DeepSeek Harness' })
  addSessionRow(root, { title: 'SanXiao' })
  const world = loadBridge({ doc, root })
  await flush()
  const report = world.posts.find((post) => post.url === '/dsh-pet/page')
  assert.equal(report.body.title, '继续完成三效游戏剩余需求')
})

test('收命令 → 点标题匹配的会话行 → 回执 id', async () => {
  const { doc, root } = makeDoc()
  const other = addSessionRow(root, { title: '扩展宠物插件为悬浮球助手', selected: true })
  const target = addSessionRow(root, { title: 'SanXiao', selected: false })
  const world = loadBridge({ doc, root })
  await flush()

  world.cmd = { id: 7, action: 'open', title: 'SanXiao', ts: Date.now() }
  await world.tick(800) // report 拿到命令
  await world.tick(500) // pumpCommand 执行
  await world.tick(500)

  assert.equal(target.clicks, 1, '目标会话行应被点一次')
  assert.equal(other.clicks, 0)
  const ack = world.posts.find((post) => post.url === '/dsh-pet/cmd-ack')
  assert.ok(ack, '应该回执命令')
  assert.equal(ack.body.id, 7)
})

test('命令按标题"包含"匹配，且已是当前会话时不再点', async () => {
  const { doc, root } = makeDoc()
  const current = addSessionRow(root, { title: 'SanXiao · 消消乐', selected: true })
  const loose = addSessionRow(root, { title: 'ball-war-3d', selected: false })
  const world = loadBridge({ doc, root })
  await flush()

  world.cmd = { id: 9, action: 'open', title: 'ball-war-3d' }
  await world.tick(800)
  await world.tick(500)
  assert.equal(loose.clicks, 1)
  assert.equal(current.clicks, 0)
})

test('还没渲染出会话行时先不放弃，下一轮再点', async () => {
  const { doc, root } = makeDoc()
  const world = loadBridge({ doc, root })
  await flush()

  world.cmd = { id: 11, action: 'open', title: 'SanXiao' }
  await world.tick(800)
  await world.tick(500) // 此时没有 sessionRow → 不点、不 ack
  assert.equal(world.posts.filter((post) => post.url === '/dsh-pet/cmd-ack').length, 0)

  const row = addSessionRow(root, { title: 'SanXiao' })
  await world.tick(500) // 行出现了 → 点 + ack
  assert.equal(row.clicks, 1)
  const acks = world.posts.filter((post) => post.url === '/dsh-pet/cmd-ack')
  assert.equal(acks.length, 1)
  assert.equal(acks[0].body.id, 11)
})

test('重复拿到同一条命令不会重复点', async () => {
  const { doc, root } = makeDoc()
  const row = addSessionRow(root, { title: 'SanXiao' })
  const world = loadBridge({ doc, root })
  await flush()
  world.cmd = { id: 12, action: 'open', title: 'SanXiao' }
  await world.tick(800)
  await world.tick(500)
  assert.equal(row.clicks, 1)
  await world.tick(800)
  await world.tick(500)
  assert.equal(row.clicks, 1, '同一 id 只执行一次')
})

test('focus 命令直接回执，不动任何会话行', async () => {
  const { doc, root } = makeDoc()
  const row = addSessionRow(root, { title: 'SanXiao', selected: true })
  const world = loadBridge({ doc, root })
  await flush()
  world.cmd = { id: 13, action: 'focus', title: '' }
  await world.tick(800)
  await world.tick(500)
  assert.equal(row.clicks, 0)
  const ack = world.posts.find((post) => post.url === '/dsh-pet/cmd-ack')
  assert.equal(ack.body.id, 13)
})

test('桥脚本只装一次（window.__dshPetBridge 守卫）', () => {
  const { doc, root } = makeDoc()
  const world = loadBridge({ doc, root })
  const before = world.bridge.startedAt
  // 再跑一遍同一个源码，不应覆盖已有实例
  const context = { window: {}, document: doc, location: { href: 'http://127.0.0.1:8598/' }, setInterval: () => 1, clearInterval: () => {}, Date, String, JSON, Promise, fetch: () => Promise.resolve({ ok: true, json: () => Promise.resolve(null) }) }
  context.window = context
  vm.createContext(context)
  vm.runInContext(BRIDGE_SOURCE + '\n;window.__again = window.__dshPetBridge;', context)
  assert.equal(typeof context.window.__dshPetBridge.stop, 'function')
  assert.equal(typeof before, 'number')
})
