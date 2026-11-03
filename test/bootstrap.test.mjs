/**
 * 验证「从市场装完就能用」：把插件当作刚被 npm install 进 profile 的包，
 * 用一个假 cordis ctx 触发 apply()，检查它是否自动把伴侣 exe 与默认素材铺到 $DSH_HOME\dsh-pet。
 *
 * 用法：node test/bootstrap.test.mjs
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const TEMP_HOME = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-pet-boot-'))
process.env.DSH_HOME = TEMP_HOME
process.env.DSH_PET_EXE = ''
process.env.DSH_PET_QUIT_FLAG = path.join(TEMP_HOME, 'quit.flag')
process.env.DSH_PET_AVATAR = ''
process.env.DSH_PET_SOUND = ''

const { default: plugin } = await import('../lib/index.js')

const PET_DIR = path.join(TEMP_HOME, 'dsh-pet')

function makeHost() {
  const routes = new Map()
  const listeners = new Map()
  const cleanups = []
  const ctx = {
    webServer: {
      port: 0,
      register(route) {
        routes.set(route.path, route)
        return () => routes.delete(route.path)
      },
      tapIndex() {},
    },
    get() {
      return undefined
    },
    logger: { info() {}, warn() {}, error() {}, debug() {} },
    on(name, fn) {
      listeners.set(name, fn)
      return () => {}
    },
    effect(fn) {
      cleanups.push(fn)
    },
    inject(_deps, fn) {
      fn(ctx)
    },
  }
  const root = {
    on(name, fn) {
      listeners.set(name, fn)
    },
    effect(fn) {
      cleanups.push(fn)
    },
    inject(_deps, fn) {
      fn(ctx)
    },
    get() {
      return undefined
    },
  }
  return { root, ctx, routes, listeners, cleanups }
}

test('首次运行把伴侣 exe 与默认素材铺到 $DSH_HOME/dsh-pet', async () => {
  const host = makeHost()
  plugin.apply(host.root)
  await new Promise((r) => setTimeout(r, 300))
  for (const name of ['DshPetFloat.exe', 'avatar.png', 'sound.wav', 'pet.wav']) {
    const p = path.join(PET_DIR, name)
    assert.ok(fs.existsSync(p), `缺少 ${name}`)
    assert.ok(fs.statSync(p).size > 1000, `${name} 太小`)
  }
  assert.ok(fs.existsSync(path.join(PET_DIR, 'DshPetFloat.exe')), 'exe 应就位')
})

test('状态文件里指向的 avatar/sound 就是铺好的默认素材', async () => {
  const host = makeHost()
  plugin.apply(host.root)
  const stateFile = path.join(TEMP_HOME, '.dshp-state.json')
  const deadline = Date.now() + 3000
  while (Date.now() < deadline && !fs.existsSync(stateFile)) {
    await new Promise((r) => setTimeout(r, 50))
  }
  assert.ok(fs.existsSync(stateFile), '状态文件应已落盘')
  const snap = JSON.parse(fs.readFileSync(stateFile, 'utf8'))
  assert.equal(path.basename(snap.host.avatarFile), 'avatar.png')
  assert.equal(path.basename(snap.host.soundFile), 'sound.wav')
  assert.equal(path.basename(snap.host.petSoundFile), 'pet.wav')
})

test('再跑一次不会覆盖用户自备的头像', async () => {
  fs.writeFileSync(path.join(PET_DIR, 'avatar.png'), Buffer.from('user-avatar'))
  const host = makeHost()
  plugin.apply(host.root)
  await new Promise((r) => setTimeout(r, 300))
  const data = fs.readFileSync(path.join(PET_DIR, 'avatar.png'), 'utf8')
  assert.equal(data, 'user-avatar')
})
