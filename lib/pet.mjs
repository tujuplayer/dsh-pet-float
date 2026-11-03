/**
 * dsh-pet 状态引擎（纯 Node，不依赖 cordis，可单独测试）
 *
 * 职责：吃 DSH 宿主会话事件 + 页面桥上报 + agent 思考流，吐出一份"当前状态"快照，
 * 由 lib/index.js 负责落盘成 %DSH_HOME%\.dshp-state.json（契约见 ../../SPEC.md 第 1 节）。
 *
 * 设计要点：
 *  1. **只跟踪"最近活跃的那个会话"**：DSH 同一时间只有一个前台会话在跑，
 *     多会话并按 updatedAt 存最多 8 条给伴侣的列表用。
 *  2. **声音事件靠 events[] 环形缓冲 + id 去重**：伴侣冷启动把已有 id 记入已见集合，不补播；
 *     之后每个新 id 播一次"啊"。这样宿主写盘抖动/伴侣重启都不会重复响。
 *  3. 事件里**没有**"为什么停下"的可靠信号（turn/end.reason 的 blocked 与提问无关），
 *     所以 status 完全由我们自己的小状态机推：
 *       ask_user_question 的 tool/call 挂起 → waiting-question，直到它的 tool/result 回来
 *       approval/asked → waiting-approval，直到 approval/decided
 *     这是"停滞需要人操作"的唯一权威来源。
 */

const MAX_EVENTS = 20
const MAX_SESSIONS = 40
const MAX_THINKING = 160
const MAX_LAST_TEXT = 160
const WAIT_QUESTION_TOOL = 'ask_user_question'
const TOOL_TEXT_MAX = 40

const sleepNow = () => Date.now()

/**
 * 会话排序：新的排前面。先比 updatedAt，同一毫秒并列时比创建顺序。
 * （同一毫秒里连开多个会话时，只按 updatedAt 排的结果不稳定，会让快照顺序抖动。）
 */
function sortByRecency(a, b) {
  if (!a || !b) return 0
  const delta = (b.updatedAt || 0) - (a.updatedAt || 0)
  if (delta) return delta
  return (b.seq || 0) - (a.seq || 0)
}

/** 压成一行、去多余空白、按码点截断（避免把 emoji 劈成半个）。 */
export function oneLine(input, max = 120) {
  const text = String(input == null ? '' : input).replace(/\s+/g, ' ').trim()
  if (!text) return ''
  const chars = Array.from(text)
  if (chars.length <= max) return text
  return chars.slice(0, max - 1).join('') + '…'
}

/** 取推理/正文里"最后一行非空"当摘要：思考流是滚动文本，最后一段才是当下在想什么。 */
export function tailLine(text, max = MAX_THINKING) {
  const raw = String(text == null ? '' : text)
  if (!raw) return ''
  const lines = raw.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  const last = lines.length ? lines[lines.length - 1] : raw
  return oneLine(last, max)
}

/** 摘要显示在球旁面板，太长没意义：只保留尾部 n 个字符。 */
function tailChars(text, max) {
  const chars = Array.from(String(text == null ? '' : text))
  if (chars.length <= max) return chars.join('')
  return chars.slice(-max).join('')
}

function num(value) {
  const n = Number(value)
  return Number.isFinite(n) && n >= 0 ? n : 0
}

/** 从 ask_user_question 的入参里抠出"人类要先读的那句话"。 */
export function describeQuestion(args) {
  try {
    let parsed = args
    if (typeof parsed === 'string') parsed = JSON.parse(parsed)
    const list = Array.isArray(parsed && parsed.questions) ? parsed.questions : []
    const first = list[0] || {}
    const text = oneLine(first.question || first.header || parsed && parsed.question, 240)
    const options = (Array.isArray(first.options) ? first.options : [])
      .map((option) => oneLine(option && (option.label || option.value || option.text), 40))
      .filter(Boolean)
      .slice(0, 6)
    if (!text) return null
    return { text, options }
  } catch (err) {
    return null
  }
}

/** 授权请求的中文说明：写清楚"要拿什么权限、为什么"。 */
export function describeApproval(data) {
  try {
    const tool = oneLine(data && (data.toolName || data.name), 60)
    const reason = oneLine(data && data.reason, 160)
    if (reason && tool) return `允许 ${tool} 执行？· ${reason}`
    if (reason) return reason
    if (tool) return `允许 ${tool} 执行？`
    return '有操作需要你授权'
  } catch (err) {
    return '有操作需要你授权'
  }
}

function statusDetail(status, phase, extra) {
  switch (status) {
    case 'running':
      if (phase === 'tool') return extra.toolName ? `调用工具：${oneLine(extra.toolName, TOOL_TEXT_MAX)}` : '调用工具中'
      return extra.step ? `思考中 · 第 ${extra.step} 步` : '思考中'
    case 'waiting-question':
      return '等你回答'
    case 'waiting-approval':
      return '等你授权'
    case 'done':
      return '本轮已完成'
    case 'error':
      return '出错了'
    case 'idle':
    default:
      return '空闲'
  }
}

export function createPetEngine(options = {}) {
  const sessions = new Map()
  /** @type {string} 最近一次有事件的会话（= 伴侣该展示的那个） */
  let activeKey = ''
  let seq = 0
  /** 会话创建顺序（用于 updatedAt 同毫秒并列时的稳定排序） */
  let sessionSeq = 0
  const events = []
  let eventSeq = 0
  let cmdSeq = 0
  /** @type {null | { id: number, action: string, title: string, ts: number }} */
  let cmd = null
  const cmdHistory = new Set()
  const page = { alive: false, lastSeenTs: 0, title: '', visible: true, focused: false, href: '' }
  let hostInfo = {}
  let virtualSession = null // 宿主还没发过事件时，用页面标题撑起一个占位会话
  let dirty = true

  function markDirty() {
    dirty = true
  }

  function ensure(key, title) {
    let s = sessions.get(key)
    if (!s) {
      s = {
        key,
        seq: (sessionSeq += 1),
        title: oneLine(title, 120),
        status: 'idle',
        phase: 'idle',
        turn: 0,
        step: 0,
        turnStartedAt: 0,
        endedAt: 0,
        waitingSince: 0,
        question: null,
        approval: null,
        todos: null,
        tokens: { turn: 0, total: 0, cached: 0 },
        lastText: '',
        thinking: '',
        thinkingSource: '',
        thinkingAt: 0,
        liveReasoning: '',
        toolName: '',
        waitCallId: '',
        updatedAt: sleepNow(),
      }
      sessions.set(key, s)
    }
    return s
  }

  function setTitle(s, title) {
    const text = oneLine(title, 120)
    if (!text || text === s.title) return false
    s.title = text
    return true
  }

  /** 会话进入"跑起来了"的状态：清掉挂起标记。 */
  function resumeRunning(s, phase) {
    s.status = 'running'
    s.phase = phase || 'thinking'
    s.waitingSince = 0
    s.question = null
    s.approval = null
    s.waitCallId = ''
    s.endedAt = 0
  }

  function addEvent(kind, s, extra) {
    eventSeq += 1
    const ev = {
      id: `ev-${eventSeq}`,
      kind,
      title: s ? oneLine(s.title, 120) : '',
      ts: sleepNow(),
    }
    if (extra && extra.text) ev.text = oneLine(extra.text, 200)
    if (extra && extra.key) ev.key = extra.key
    events.push(ev)
    while (events.length > MAX_EVENTS) events.shift()
    markDirty()
    return ev
  }

  /** 宿主会话事件（ctx.on('session/event') 给的那两个参数） */
  function onSessionEvent(session, event) {
    try {
      if (!event || !event.type) return
      const key = session && session.id ? String(session.id) : 'default'
      const data = (event && event.data) || {}
      let serviceTitle = ''
      try {
        if (typeof options.resolveTitle === 'function') serviceTitle = oneLine(options.resolveTitle(session), 120)
      } catch (err) {}
      const title = titleOf(session, data, event) || serviceTitle
      const s = ensure(key, title)
      if (title) setTitle(s, title)
      activeKey = key
      virtualSession = null
      const type = String(event.type)

      switch (type) {
        case 'turn/start': {
          s.turn = num(data.turn)
          s.step = num(data.step)
          s.turnStartedAt = sleepNow()
          s.tokens.turn = 0
          s.liveReasoning = ''
          s.toolName = ''
          resumeRunning(s, 'thinking')
          break
        }
        case 'step/start': {
          if (data.turn !== undefined) s.turn = num(data.turn)
          if (data.step !== undefined) s.step = num(data.step)
          if (s.status === 'running' || s.status === 'idle') resumeRunning(s, 'thinking')
          break
        }
        case 'step/end': {
          if (data.step !== undefined) s.step = num(data.step)
          break
        }
        case 'tool/call': {
          const name = String(data.name || data.toolName || '')
          s.toolName = name
          if (name === WAIT_QUESTION_TOOL) {
            const q = describeQuestion(data.arguments)
            s.status = 'waiting-question'
            s.phase = 'waiting'
            s.waitingSince = sleepNow()
            s.turnStartedAt = s.turnStartedAt || sleepNow()
            s.question = q || { text: '有提问等你回答', options: [] }
            s.approval = null
            s.waitCallId = String(data.callId || '')
            addEvent('question', s, { text: s.question.text, key })
            break
          }
          if (s.status !== 'waiting-question' && s.status !== 'waiting-approval') {
            resumeRunning(s, 'tool')
          }
          break
        }
        case 'tool/result': {
          const callId = String((data.message && data.message.callId) || data.callId || '')
          if (s.status === 'waiting-question' && (!s.waitCallId || !callId || callId === s.waitCallId)) {
            resumeRunning(s, 'thinking')
            s.liveReasoning = ''
          }
          break
        }
        case 'approval/asked': {
          s.status = 'waiting-approval'
          s.phase = 'waiting'
          s.waitingSince = sleepNow()
          s.approval = { text: describeApproval(data) }
          s.question = null
          addEvent('approval', s, { text: s.approval.text, key })
          break
        }
        case 'approval/decided': {
          if (s.status === 'waiting-approval') {
            resumeRunning(s, 'thinking')
            s.liveReasoning = ''
          }
          break
        }
        case 'assistant/message': {
          const message = data.message || {}
          const usage = data.usage || message.usage || null
          if (usage) {
            const input = num(usage.inputTokens)
            const cached = num(usage.cacheReadTokens || usage.cachedTokens)
            const output = num(usage.outputTokens)
            s.tokens.turn = input + cached + output
            s.tokens.total += s.tokens.turn
            s.tokens.cached += cached
          }
          const blocks = Array.isArray(message.content) ? message.content : []
          let lastText = ''
          let lastReasoning = ''
          for (const block of blocks) {
            if (!block || !block.type) continue
            if (block.type === 'text' && oneLine(block.text)) lastText = String(block.text)
            if (block.type === 'reasoning' && oneLine(block.text)) lastReasoning = String(block.text)
          }
          if (lastText) s.lastText = oneLine(lastText, MAX_LAST_TEXT)
          if (lastReasoning) {
            s.thinking = tailLine(lastReasoning)
            s.thinkingSource = 'host'
            s.thinkingAt = sleepNow()
            s.liveReasoning = ''
          }
          break
        }
        case 'todo/write': {
          const list = Array.isArray(data.todos) ? data.todos : []
          if (!list.length) {
            s.todos = null
            break
          }
          const done = list.filter((t) => t && (t.status === 'completed')).length
          const currentTodo = list.find((t) => t && t.status === 'in_progress')
          s.todos = {
            done,
            total: list.length,
            current: oneLine(currentTodo && currentTodo.content, 80),
          }
          break
        }
        case 'turn/end': {
          const reason = data.reason || {}
          s.endedAt = sleepNow()
          s.status = reason && reason.kind === 'error' ? 'error' : 'done'
          s.phase = s.status === 'error' ? 'done' : 'done'
          s.waitingSince = 0
          s.question = null
          s.approval = null
          s.waitCallId = ''
          s.toolName = ''
          s.liveReasoning = ''
          addEvent('turn-done', s, { key })
          break
        }
        case 'session/title': {
          setTitle(s, data.title)
          break
        }
        default:
          break
      }
      s.updatedAt = sleepNow()
      markDirty()
    } catch (err) {
      // 状态机绝不让宿主事件链因为本插件出错而中断
    }
  }

  /** agent/assistant-stream：实时思考流（未落盘的推理帧） */
  function onStream(payload) {
    try {
      if (!payload) return
      const agent = payload.agent
      const session = agent && agent.session
      const key = session && session.id ? String(session.id) : ''
      const frame = payload.frame
      if (!frame || !frame.type) return
      if (!key) return
      const s = ensure(key, '')
      if (frame.type === 'start') {
        s.liveReasoning = ''
        s.toolName = ''
        if (s.status !== 'waiting-question' && s.status !== 'waiting-approval') resumeRunning(s, 'thinking')
        s.turnStartedAt = s.turnStartedAt || sleepNow()
        if (frame.turn !== undefined) s.turn = num(frame.turn)
        if (frame.step !== undefined) s.step = num(frame.step)
        activeKey = key
        s.updatedAt = sleepNow()
        markDirty()
        return
      }
      if (frame.type !== 'chunk') return
      const chunk = frame.chunk || {}
      const now = sleepNow()
      if (chunk.type === 'reasoning-delta' && chunk.text) {
        s.liveReasoning = tailChars(s.liveReasoning + chunk.text, 2000)
        const line = tailLine(s.liveReasoning)
        if (line) {
          s.thinking = line
          s.thinkingSource = 'host'
          s.thinkingAt = now
        }
        if (s.status === 'running') s.phase = 'thinking'
      } else if (chunk.type === 'tool-call' || chunk.type === 'tool-call-delta') {
        if (s.status === 'running') s.phase = 'tool'
      }
      if (s.status === 'running' && !s.turnStartedAt) s.turnStartedAt = now
      s.updatedAt = now
      markDirty()
    } catch (err) {}
  }

  /** 页面桥每 ~800ms 的一次上报：会话标题 + 当前思考摘要 + 页面可见性 */
  function onPageReport(report) {
    const now = sleepNow()
    const incoming = report && typeof report === 'object' ? report : {}
    page.alive = true
    page.lastSeenTs = now
    page.visible = incoming.visible !== false
    page.focused = incoming.focused === true
    if (typeof incoming.href === 'string') page.href = incoming.href.slice(0, 300)
    const pageTitle = oneLine(incoming.title, 120)
    if (pageTitle) page.title = pageTitle
    const pageThinking = oneLine(incoming.thinking, MAX_THINKING)

    const active = sessions.get(activeKey)
    if (active) {
      if (pageTitle) setTitle(active, pageTitle)
      // 页面能直接读到"正在思考"的折叠摘要（host 流没帧时尤其有用）
      if (pageThinking && now - active.thinkingAt > 1200) {
        active.thinking = pageThinking
        active.thinkingSource = 'page'
        active.thinkingAt = now
      }
      active.updatedAt = now
    } else if (pageTitle || virtualSession) {
      // 宿主还没发过任何会话事件（插件刚装好/刚重启）：先用页面标题撑个占位会话，
      // 这样伴侣一启动就不是空的，也不会误报"运行中"。
      virtualSession = {
        key: 'page',
        title: pageTitle || (virtualSession && virtualSession.title) || '',
        thinking: pageThinking || (virtualSession && virtualSession.thinking) || '',
        status: 'idle',
      }
    }
    markDirty()
    return { ok: true, cmd: currentCmd() }
  }

  function currentCmd() {
    return cmd ? { ...cmd } : null
  }

  /** 页面执行完命令回执 */
  function ackCmd(id) {
    const numeric = Number(id)
    if (!cmd) return { ok: true, cleared: false }
    if (Number.isFinite(numeric) && numeric === cmd.id) {
      cmdHistory.add(cmd.id)
      cmd = null
      markDirty()
      return { ok: true, cleared: true }
    }
    return { ok: true, cleared: false }
  }

  /** 伴侣请求"打开/置前"：写一条命令，页面桥下次上报时取走 */
  function requestOpen(title, action) {
    cmdSeq += 1
    const text = oneLine(title, 120)
    cmd = { id: cmdSeq, action: action || (text ? 'open' : 'focus'), title: text, ts: sleepNow() }
    markDirty()
    return { ...cmd }
  }

  function setHostInfo(info) {
    hostInfo = info && typeof info === 'object' ? info : {}
    markDirty()
  }

  function elapsedOf(s, now) {
    if (!s.turnStartedAt) return 0
    if (s.status === 'waiting-question' || s.status === 'waiting-approval') {
      return Math.max(0, (s.waitingSince || now) - s.turnStartedAt)
    }
    if (s.status === 'done' || s.status === 'error') {
      return Math.max(0, (s.endedAt || now) - s.turnStartedAt)
    }
    if (s.status === 'running') return Math.max(0, now - s.turnStartedAt)
    return 0
  }

  function publicSession(s, now, active) {
    return {
      key: s.key,
      title: s.title,
      status: s.status,
      phase: s.phase,
      detail: statusDetail(s.status, s.phase, s),
      thinking: s.thinking || '',
      thinkingSource: s.thinkingSource || '',
      turn: s.turn,
      step: s.step,
      turnStartedAt: s.turnStartedAt || 0,
      elapsedMs: elapsedOf(s, now),
      waitingSince: s.waitingSince || 0,
      question: s.question || null,
      approval: s.approval || null,
      todos: s.todos || null,
      tokens: { turn: s.tokens.turn, total: s.tokens.total, cached: s.tokens.cached },
      lastText: s.lastText || '',
      updatedAt: s.updatedAt,
      current: active,
    }
  }

  /** 组装契约里的完整状态对象（写盘前调用） */
  function snapshot(extra) {
    const now = sleepNow()
    page.alive = page.lastSeenTs > 0 && now - page.lastSeenTs < 6000
    const keys = [...sessions.keys()]
    keys.sort((a, b) => sortByRecency(sessions.get(a), sessions.get(b)))
    const list = keys.slice(0, 8)
    let active = sessions.get(activeKey)
    // 活跃会话被挤出 top8 也照常作为 active
    const activeView = active ? publicSession(active, now, true) : (virtualSession ? {
      key: virtualSession.key,
      title: virtualSession.title,
      status: 'idle',
      phase: 'idle',
      detail: '空闲',
      thinking: virtualSession.thinking || '',
      thinkingSource: virtualSession.thinking ? 'page' : '',
      turn: 0,
      step: 0,
      turnStartedAt: 0,
      elapsedMs: 0,
      waitingSince: 0,
      question: null,
      approval: null,
      todos: null,
      tokens: { turn: 0, total: 0, cached: 0 },
      lastText: '',
      updatedAt: page.lastSeenTs || 0,
      current: true,
    } : null)
    if (activeView && page.title && !activeView.title) activeView.title = page.title

    const sessionsOut = list.map((key) => {
      const s = sessions.get(key)
      return {
        key: s.key,
        title: s.title,
        status: s.status,
        updatedAt: s.updatedAt,
        current: s.key === activeKey,
      }
    })
    if (activeView && !sessionsOut.some((row) => row.key === activeView.key)) {
      sessionsOut.unshift({
        key: activeView.key,
        title: activeView.title,
        status: activeView.status,
        updatedAt: activeView.updatedAt,
        current: true,
      })
    }

    let pending = 0
    let running = 0
    for (const s of sessions.values()) {
      if (s.status === 'waiting-question' || s.status === 'waiting-approval') pending += 1
      else if (s.status === 'running') running += 1
    }

    seq += 1
    dirty = false
    return {
      schema: 1,
      seq,
      ts: now,
      host: {
        version: String(hostInfo.version || ''),
        pid: Number(hostInfo.pid || 0),
        electronPid: Number(hostInfo.electronPid || 0),
        port: Number(hostInfo.port || 0),
        avatarFile: String(hostInfo.avatarFile || ''),
        soundFile: String(hostInfo.soundFile || ''),
        petSoundFile: String(hostInfo.petSoundFile || ''),
        stateFile: String(hostInfo.stateFile || ''),
        cmdFile: String(hostInfo.cmdFile || ''),
      },
      page: { ...page },
      active: activeView,
      sessions: sessionsOut.slice(0, 8),
      events: events.slice(-MAX_EVENTS),
      counts: { pending, running },
      ...(extra || {}),
    }
  }

  function isDirty() {
    return dirty
  }

  function knownEventIds() {
    return events.map((ev) => ev.id)
  }

  function prune(nowMs) {
    // 会话表别无限长：保留最近 1 小时的 40 个
    if (sessions.size <= MAX_SESSIONS) return
    const rows = [...sessions.values()].sort((a, b) => sortByRecency(a, b))
    for (const row of rows.slice(MAX_SESSIONS)) {
      if (row.key === activeKey) continue
      if (nowMs && nowMs - (row.updatedAt || 0) < 3600000) continue
      sessions.delete(row.key)
    }
  }

  return {
    onSessionEvent,
    onStream,
    onPageReport,
    ackCmd,
    requestOpen,
    currentCmd,
    setHostInfo,
    snapshot,
    isDirty,
    knownEventIds,
    prune,
    // 测试用
    _sessions: sessions,
    _page: page,
  }
}

/** 会话标题：sessionTitle 服务优先（能拿到插件启动前就有的标题），再退事件/会话字段 */
function titleOf(session, data, event) {
  try {
    if (event && event.type === 'session/title' && data && data.title) return oneLine(data.title, 120)
    const cands = [
      data && data.title,
      session && session.name,
      session && session.title,
      session && session.label,
    ]
    for (const c of cands) {
      const text = oneLine(c, 120)
      if (text) return text
    }
  } catch (err) {}
  return ''
}
