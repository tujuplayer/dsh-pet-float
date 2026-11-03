/**
 * dsh-pet 页面桥（注入 DSH 页面）
 *
 * 只做两件事，都很轻：
 *   1. 读 DOM：当前会话标题、当前思考摘要，每 800ms POST /dsh-pet/page 上报心跳；
 *   2. 收命令：宿主把桌面伴侣写的 .dshp-cmd.json 带回来（{action:'open', title}），
 *      在会话列表里找到标题匹配的那一行并点它 —— DSH 前端没有 URL 路由（纯 React state），
 *      点 DOM 是唯一可行的切会话方式。
 *
 * 全部选择器都用 CSS Modules 的**后缀**匹配（[class*="_sessionRow"]），因为类名 hash
 * 会随 DSH 构建变化，但 `_sessionRow` / `_title` / `_selected` 后缀是稳定语义。
 */
(function () {
  'use strict'
  if (window.__dshPetBridge) return
  window.__dshPetBridge = { version: 1, startedAt: Date.now() }

  var HEARTBEAT_MS = 800
  var CMD_RETRY_MS = 500
  var CMD_GIVEUP_MS = 6000

  var lastExecuted = 0
  var pendingCmd = null
  var pendingSince = 0
  var timers = []

  function text(node) {
    if (!node) return ''
    return String(node.textContent || '').replace(/\s+/g, ' ').trim()
  }

  function oneLine(value, max) {
    var out = String(value == null ? '' : value).replace(/\s+/g, ' ').trim()
    if (out.length <= max) return out
    return out.slice(0, max - 1) + '…'
  }

  /** 当前会话标题：优先"选中态"的会话行，其次 document.title 去掉 " — DeepSeek Harness" */
  function readTitle() {
    try {
      var rows = document.querySelectorAll('[class*="_sessionRow"]')
      for (var i = 0; i < rows.length; i += 1) {
        var row = rows[i]
        if (!/_selected/.test(String(row.className || ''))) continue
        var title = text(row.querySelector('[class*="_title"]')) || text(row)
        if (title) return oneLine(title, 120)
      }
      var doc = document.title || ''
      var cut = doc.split(' — ')
      if (cut.length > 1) return oneLine(cut[0], 120)
      if (doc.indexOf('DeepSeek Harness') === -1) return oneLine(doc, 120)
    } catch (err) {}
    return ''
  }

  /** 当前思考：最后一个 [data-variant="think"]，只在 data-state=running 时取摘要 */
  function readThinking() {
    try {
      var rows = document.querySelectorAll('[data-variant="think"]')
      if (!rows.length) return ''
      var last = rows[rows.length - 1]
      var state = String(last.getAttribute('data-state') || '')
      if (state && state !== 'running') return ''
      var summary = last.querySelector('[class*="_summaryText"]') || last.querySelector('[class*="summaryText"]')
      var value = text(summary)
      if (!value) {
        // 展开态没有 summaryText：取整块文本的第一行（不带展开的全文，太长会没意义）
        var full = text(last)
        var lines = full.split(/[\n。]/).filter(Boolean)
        value = lines.length ? lines[0] : full
      }
      return oneLine(value, 160)
    } catch (err) {
      return ''
    }
  }

  /** 点标题匹配的会话行。返回 true = 已经点过（或本来就是当前会话） */
  function openSession(title) {
    var want = oneLine(title, 120).toLowerCase()
    var rows = document.querySelectorAll('[class*="_sessionRow"]')
    if (!rows.length) return false
    var exact = null
    var loose = null
    for (var i = 0; i < rows.length; i += 1) {
      var row = rows[i]
      var rowTitle = text(row.querySelector('[class*="_title"]')) || text(row)
      if (!rowTitle) continue
      var lower = rowTitle.toLowerCase()
      if (want && lower === want) exact = row
      if (!loose && want && (lower.indexOf(want) !== -1 || want.indexOf(lower) !== -1)) loose = row
    }
    var target = exact || loose
    if (!target) return false
    if (/_selected/.test(String(target.className || ''))) return true
    try {
      target.click()
      return true
    } catch (err) {
      return false
    }
  }

  function post(url, body) {
    try {
      return fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {}),
        credentials: 'same-origin',
        cache: 'no-store',
      }).then(function (res) {
        return res.ok ? res.json() : null
      }).catch(function () {
        return null
      })
    } catch (err) {
      return Promise.resolve(null)
    }
  }

  function report() {
    var payload = {
      title: readTitle(),
      thinking: readThinking(),
      visible: !document.hidden,
      focused: document.hasFocus(),
      href: String(location.href || ''),
      at: Date.now(),
    }
    post('/dsh-pet/page', payload).then(function (reply) {
      if (!reply || !reply.cmd) return
      var cmd = reply.cmd
      if (!cmd || !cmd.id || cmd.id === lastExecuted) return
      if (pendingCmd && pendingCmd.id === cmd.id) return
      pendingCmd = cmd
      pendingSince = Date.now()
      try {
        window.__dshPetBridge.lastCmd = cmd
      } catch (err) {}
    })
  }

  /** 命令执行循环：切会话可能因为列表还没渲染出来失败，500ms 重试，6s 后放弃 */
  function pumpCommand() {
    if (!pendingCmd) return
    var cmd = pendingCmd
    var ok = false
    if (cmd.action === 'focus') {
      ok = true
    } else {
      ok = openSession(cmd.title)
      // 目标标题为空（伴侣只说"打开"）：不做切换，只当已处理
      if (!ok && !oneLine(cmd.title, 120)) ok = true
    }
    if (!ok && Date.now() - pendingSince < CMD_GIVEUP_MS) return
    lastExecuted = cmd.id
    pendingCmd = null
    post('/dsh-pet/cmd-ack', { id: cmd.id, ok: ok }).then(function () {})
  }

  function start() {
    try {
      timers.push(setInterval(report, HEARTBEAT_MS))
      timers.push(setInterval(pumpCommand, CMD_RETRY_MS))
      if (document.addEventListener) {
        document.addEventListener('visibilitychange', report)
      }
      report()
    } catch (err) {}
  }

  window.__dshPetBridge.stop = function () {
    for (var i = 0; i < timers.length; i += 1) clearInterval(timers[i])
    timers = []
  }
  window.__dshPetBridge.readTitle = readTitle
  window.__dshPetBridge.readThinking = readThinking
  window.__dshPetBridge.openSession = openSession

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start)
  } else {
    start()
  }
})()
