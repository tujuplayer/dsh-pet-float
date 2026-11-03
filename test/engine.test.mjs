/**
 * 状态引擎单测：node --test 或直接 node test/engine.test.mjs
 * 覆盖 SPEC 第 1 节里伴侣依赖的全部字段与状态迁移。
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createPetEngine, oneLine, tailLine, describeQuestion, describeApproval } from '../lib/pet.mjs'

const session = (id) => ({ id, name: '' })
const ev = (type, data) => ({ type, data })

function feed(engine, sessionObj, type, data) {
  engine.onSessionEvent(sessionObj, ev(type, data))
}

test('oneLine / tailLine 截断', () => {
  assert.equal(oneLine('  a \n b  '), 'a b')
  assert.equal(oneLine('abcdef', 4), 'abc…')
  assert.equal(tailLine('第一行\n第二行\n\n第三行'), '第三行')
})

test('describeQuestion 抠出问题与选项', () => {
  const q = describeQuestion({
    questions: [{ id: 'q1', question: '要不要继续？', options: [{ label: '继续' }, { label: '暂停' }] }],
  })
  assert.deepEqual(q, { text: '要不要继续？', options: ['继续', '暂停'] })
  assert.equal(describeQuestion('not json'), null)
})

test('describeApproval 中文说明', () => {
  assert.equal(describeApproval({ toolName: 'bash', reason: '要跑测试' }), '允许 bash 执行？· 要跑测试')
  assert.equal(describeApproval({ toolName: 'write' }), '允许 write 执行？')
  assert.equal(describeApproval({}), '有操作需要你授权')
})

test('turn/start → running；ask_user_question → waiting-question；tool/result 解除', () => {
  const engine = createPetEngine()
  const s = session('s1')
  feed(engine, s, 'turn/start', { turn: 2 })
  let snap = engine.snapshot()
  assert.equal(snap.active.status, 'running')
  assert.equal(snap.active.phase, 'thinking')
  assert.equal(snap.active.turn, 2)

  feed(engine, s, 'tool/call', {
    turn: 2,
    step: 3,
    callId: 'call-1',
    name: 'ask_user_question',
    arguments: { questions: [{ id: 'q1', question: '先做哪个？', options: [{ label: 'A' }, { label: 'B' }] }] },
  })
  snap = engine.snapshot()
  assert.equal(snap.active.status, 'waiting-question')
  assert.equal(snap.active.phase, 'waiting')
  assert.equal(snap.active.question.text, '先做哪个？')
  assert.deepEqual(snap.active.question.options, ['A', 'B'])
  assert.ok(snap.active.waitingSince > 0)
  assert.equal(snap.counts.pending, 1)
  assert.equal(snap.events.filter((e) => e.kind === 'question').length, 1)

  feed(engine, s, 'tool/result', { turn: 2, step: 3, message: { callId: 'call-1', content: [], isError: false } })
  snap = engine.snapshot()
  assert.equal(snap.active.status, 'running')
  assert.equal(snap.active.question, null)
  assert.equal(snap.counts.pending, 0)
})

test('approval/asked → waiting-approval；approval/decided 解除', () => {
  const engine = createPetEngine()
  const s = session('s1')
  feed(engine, s, 'turn/start', { turn: 1 })
  feed(engine, s, 'approval/asked', { id: 'a-1', toolName: 'bash', reason: '要被允许' })
  let snap = engine.snapshot()
  assert.equal(snap.active.status, 'waiting-approval')
  assert.match(snap.active.approval.text, /bash/)
  assert.equal(snap.events.filter((e) => e.kind === 'approval').length, 1)

  feed(engine, s, 'approval/decided', { id: 'a-1', outcome: 'allowed-once' })
  snap = engine.snapshot()
  assert.equal(snap.active.status, 'running')
  assert.equal(snap.active.approval, null)
})

test('turn/end → done，出错 → error，且都产生 turn-done 事件', () => {
  const engine = createPetEngine()
  const s = session('s1')
  feed(engine, s, 'turn/start', { turn: 4 })
  feed(engine, s, 'turn/end', { turn: 4, reason: { kind: 'completed' } })
  let snap = engine.snapshot()
  assert.equal(snap.active.status, 'done')
  assert.equal(snap.active.phase, 'done')
  assert.equal(snap.events.filter((e) => e.kind === 'turn-done').length, 1)
  assert.ok(snap.active.elapsedMs >= 0)

  feed(engine, s, 'turn/start', { turn: 5 })
  feed(engine, s, 'turn/end', { turn: 5, reason: { kind: 'error', error: { message: 'boom' } } })
  snap = engine.snapshot()
  assert.equal(snap.active.status, 'error')
})

test('assistant/message 带 usage → tokens / lastText / thinking；tool call 换 phase', () => {
  const engine = createPetEngine()
  const s = session('s1')
  feed(engine, s, 'turn/start', { turn: 1 })
  feed(engine, s, 'tool/call', { turn: 1, step: 2, callId: 'c2', name: 'bash', arguments: {} })
  let snap = engine.snapshot()
  assert.equal(snap.active.phase, 'tool')
  assert.match(snap.active.detail, /bash/)

  feed(engine, s, 'tool/result', { turn: 1, step: 2, message: { callId: 'c2', content: [], isError: false } })
  feed(engine, s, 'assistant/message', {
    turn: 1,
    step: 2,
    message: {
      content: [
        { type: 'reasoning', text: '先看看目录结构\n然后把状态机写出来' },
        { type: 'text', text: '小肥鱼，已经写好了。' },
      ],
    },
    usage: { inputTokens: 1000, cacheReadTokens: 500, outputTokens: 200 },
  })
  snap = engine.snapshot()
  assert.equal(snap.active.thinking, '然后把状态机写出来')
  assert.equal(snap.active.thinkingSource, 'host')
  assert.equal(snap.active.lastText, '小肥鱼，已经写好了。')
  assert.equal(snap.active.tokens.turn, 1700)
  assert.equal(snap.active.tokens.cached, 500)
})

test('实时思考流 reasoning-delta 覆盖 thinking（source=host）', () => {
  const engine = createPetEngine()
  const s = session('s1')
  engine.onStream({ agent: { session: s }, frame: { type: 'start', turn: 1, step: 1 } })
  engine.onStream({ agent: { session: s }, frame: { type: 'chunk', chunk: { type: 'reasoning-delta', text: '正在分析' } } })
  engine.onStream({ agent: { session: s }, frame: { type: 'chunk', chunk: { type: 'reasoning-delta', text: '落盘的锁' } } })
  const snap = engine.snapshot()
  assert.equal(snap.active.status, 'running')
  assert.equal(snap.active.thinking, '正在分析落盘的锁')
  assert.equal(snap.active.thinkingSource, 'host')
})

test('页面上报补标题与思考（宿主思考过期时）', () => {
  const engine = createPetEngine()
  const s = session('s1')
  feed(engine, s, 'turn/start', { turn: 1 })
  let snap = engine.snapshot()
  assert.equal(snap.active.title, '')
  const result = engine.onPageReport({
    title: '扩展宠物插件为悬浮球助手',
    thinking: '在读 DOM 结构',
    visible: true,
    focused: true,
    href: 'http://127.0.0.1:8598/',
  })
  assert.equal(result.ok, true)
  snap = engine.snapshot()
  assert.equal(snap.active.title, '扩展宠物插件为悬浮球助手')
  assert.equal(snap.active.thinking, '在读 DOM 结构')
  assert.equal(snap.active.thinkingSource, 'page')
  assert.equal(snap.page.alive, true)
  assert.equal(snap.page.focused, true)
})

test('todo/write 汇总进度', () => {
  const engine = createPetEngine()
  const s = session('s1')
  feed(engine, s, 'turn/start', { turn: 1 })
  feed(engine, s, 'todo/write', {
    todos: [
      { content: 'A', status: 'completed' },
      { content: '写 C# 悬浮球', status: 'in_progress' },
      { content: 'C', status: 'pending' },
    ],
  })
  const snap = engine.snapshot()
  assert.deepEqual(snap.active.todos, { done: 1, total: 3, current: '写 C# 悬浮球' })
})

test('sessions 列表：最多 8 条、按 updatedAt 倒序、current 标记', () => {
  const engine = createPetEngine()
  for (let i = 0; i < 10; i += 1) {
    feed(engine, session('s' + i), 'turn/start', { turn: 1 })
    feed(engine, session('s' + i), 'turn/end', { turn: 1, reason: { kind: 'completed' } })
  }
  const snap = engine.snapshot()
  assert.equal(snap.sessions.length, 8)
  assert.equal(snap.sessions[0].key, 's9')
  assert.equal(snap.sessions[0].current, true)
  assert.equal(snap.sessions.filter((row) => row.current).length, 1)
})

test('events 环形缓冲最多 20 条且 id 唯一', () => {
  const engine = createPetEngine()
  for (let i = 0; i < 30; i += 1) {
    feed(engine, session('s1'), 'turn/end', { turn: i + 1, reason: { kind: 'completed' } })
  }
  const snap = engine.snapshot()
  assert.equal(snap.events.length, 20)
  const ids = new Set(snap.events.map((event) => event.id))
  assert.equal(ids.size, 20)
})

test('命令：requestOpen 生成、ackCmd 清除', () => {
  const engine = createPetEngine()
  const cmd = engine.requestOpen('扩展宠物插件为悬浮球助手')
  assert.equal(cmd.id, 1)
  assert.equal(cmd.action, 'open')
  assert.equal(engine.currentCmd().id, 1)
  const ack = engine.ackCmd(1)
  assert.equal(ack.cleared, true)
  assert.equal(engine.currentCmd(), null)
})

test('快照序列化后是合法 JSON，seq 递增', () => {
  const engine = createPetEngine()
  engine.setHostInfo({ pid: 1, electronPid: 2, port: 8598, avatarFile: 'x.png', soundFile: 'y.wav' })
  const first = engine.snapshot()
  const second = engine.snapshot()
  assert.equal(second.seq, first.seq + 1)
  assert.equal(first.host.port, 8598)
  assert.doesNotThrow(() => JSON.parse(JSON.stringify(second)))
})

test('没有会话时 active 为 null，页面标题撑起占位会话', () => {
  const engine = createPetEngine()
  assert.equal(engine.snapshot().active, null)
  engine.onPageReport({ title: '某个会话', visible: true })
  const snap = engine.snapshot()
  assert.equal(snap.active.key, 'page')
  assert.equal(snap.active.title, '某个会话')
  assert.equal(snap.active.status, 'idle')
})
