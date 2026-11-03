# DSH 桌面宠物 · 桌面伴侣进程（DshPetFloat）

小肥鱼的 DSH 桌面宠物悬浮球。这个目录是**桌面伴侣进程**：一个独立的 C# WinForms 小程序，
常驻托盘、在桌面右下角画一颗状态气泡球，悬停弹出状态面板，点一下就切到 DSH。

它自己**不采集任何数据**——DSH 宿主插件每 200ms 把状态写到 JSON 文件，伴侣只读文件、
只画界面；用户点球想切会话时，伴侣往命令文件里写一条 `open`，宿主读到后再置前 DSH 窗口。

契约见上一级的 `../SPEC.md`（第 1、2 节是两个 JSON 文件格式，第 3 节是本进程的需求）。
**本进程严格按契约字段名实现，没有改任何字段名。**

---

## 1. 编译

环境：Windows 11 + .NET Framework 4.8（系统自带，无需安装 SDK / 不依赖 NuGet）。

```cmd
cd dsh-pet\companion
build.cmd
```

产物：`bin\DshPetFloat.exe`（`/target:winexe`，双击不弹黑框）。

`build.cmd` 实际调用的就是系统自带的 C# 5 编译器：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /langversion:5 /target:winexe ^
    /out:bin\DshPetFloat.exe BallWindow.cs Json.cs Model.cs Native.cs PanelWindow.cs PetApp.cs PetArt.cs Program.cs Util.cs
```

> 注意：只引用 .NET Framework 自带程序集（csc.rsp 默认已带 System / System.Drawing /
> System.Windows.Forms / System.Core），不引用第三方库。JSON 是自己手写的小解析器（`Json.cs`）。
>
> `build.cmd` 文件本身是 **GBK(936)** 编码——cmd.exe 按本地代码页读批处理，存成 UTF-8 会把
> 中文注释读乱并报奇怪的「不是内部或外部命令」。用编辑器改它时请保持 GBK。

---

## 2. 运行

### 正常模式（读真实状态）

```cmd
bin\DshPetFloat.exe
```

默认读 `%USERPROFILE%\.dsh\.dshp-state.json`（即 `$DSH_HOME/.dshp-state.json`），
命令写 `%USERPROFILE%\.dsh\.dshp-cmd.json`。DSH 没在写状态文件也不会报错——
球会变成半透明灰（离线态），等宿主上线后自动恢复。

### 演示模式（不碰真实状态，也不写命令）

```cmd
bin\DshPetFloat.exe --demo sample-state.json             :: running（思考中）
bin\DshPetFloat.exe --demo sample-state-question.json    :: waiting-question（等你回答）
bin\DshPetFloat.exe --demo sample-state-done.json        :: done（已完成）
```

演示模式：只读演示状态文件、**不写命令文件**、**不改 `%APPDATA%` 里的配置**，
适合截图、调样式、给朋友看。演示状态里的 `ts` 是静态值，所以演示模式不看 `ts` 超时
（否则静态快照一启动就被判离线）。

### 命令行参数

| 参数 | 说明 |
| --- | --- |
| `--state <path>` | 覆盖状态文件路径（默认 `%USERPROFILE%\.dsh\.dshp-state.json`） |
| `--cmd <path>` | 覆盖命令文件路径（默认 `%USERPROFILE%\.dsh\.dshp-cmd.json`） |
| `--demo <path>` | 演示模式：读指定演示状态；不写命令、不写配置 |
| `--selftest-render <dir>` | **隐藏自测开关**：不建窗口，离屏渲染 8 张球图 + 8 张面板图到目录，写 `render-manifest.txt`，然后退出 |
| `--selftest-open` | **隐藏自测开关**：启动后 1.2 秒写一条 `open` 命令，再 0.6 秒自动退出（用来验证命令文件格式） |
| `--selftest-quit` | **隐藏自测开关**：启动后 1.2 秒走一次「用户主动退出」路径（写 `quit.flag` 后退出），用来验证退出标记 |

`--selftest-render` 不创建任何窗口、不占单实例、不需要显示器焦点，可以在任何环境跑：

```cmd
bin\DshPetFloat.exe --selftest-render bin\render
```

`--selftest-open` 会短暂出现小球（约 2 秒）后自动退出：

```cmd
bin\DshPetFloat.exe --state sample-state.json --cmd bin\selftest-cmd.json --selftest-open
type bin\selftest-cmd.json
```

输出形如：

```json
{
  "id": 1,
  "action": "open",
  "title": "实现桌面宠物悬浮球的状态面板与交互",
  "ts": 1793716456244
}
```

`--selftest-quit` 用来验证「用户手动退出」标记（约 1.2 秒后退出）：

```cmd
bin\DshPetFloat.exe --selftest-quit
type "%APPDATA%\dsh-pet\quit.flag"
```

输出形如：

```json
{
  "ts": 1793716919032,
  "reason": "user"
}
```

---

## 3. 交互

- **悬停小球**：球放大到 1.06 倍，左侧弹出状态面板，离开 600ms 后淡出。
- **单击小球**（<4px 位移、<350ms）：写一条 `open` 命令并置前 DSH 窗口（`clickOpens=false` 可关掉切会话，只弹面板）。
- **拖动小球**（位移 ≥4px）：松手后位置存进配置，下次启动回到原位。
- **右键小球 / 右键托盘图标**：打开 DSH、静音/取消静音、开机自启（勾选状态）、隐藏小球、退出。
- **左键托盘图标**：显示/隐藏小球（隐藏后只能从托盘右键菜单里的「退出」结束）。
- **面板标题行**：点一下展开/收起会话列表（最多 6 条，当前会话高亮），点某条会话 = 切过去。
- **面板右下角**：`打开`（同单击小球）、`静音`、`×`（只隐藏面板，不退出）。
- **关闭**：主窗口的「关闭」= 隐藏到托盘，不会退出进程；真要退出用托盘菜单的「退出」。
  从菜单「退出」会写 `quit.flag`，宿主看门狗见标记就不再自动拉起（见下节「主动退出标记」）。

透明区域（球体外、圆角外）是**逐像素点击穿透**的：点那里等于点桌面，不会挡住底下的窗口。

状态环颜色：

| 状态 | 颜色 | 角标 |
| --- | --- | --- |
| idle | 灰 | — |
| running | 蓝（呼吸 + 旋转弧） | — |
| waiting-question | 橙 | 右上 `?` 脉冲 |
| waiting-approval | 红 | 右上 `!` |
| done | 绿 | — |
| error | 深红 | — |
| 离线（状态文件读不到 / 超时 >8s / JSON 坏） | 半透明灰 | — |

---

## 4. 配置与文件位置

配置：`%APPDATA%\dsh-pet\config.json`（首次运行自动创建，原子写；坏值自动回落默认）

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `posX` / `posY` | `-1` / `-1` | 球左上角屏幕坐标；`-1` = 默认右下角（距右 48、距下 96 DIP） |
| `scale` | `1.0` | 界面缩放，夹在 0.6 ~ 2.0 |
| `soundOn` | `true` | 总开关；静音后任何提示音都不播 |
| `endSoundOn` | `true` | 「一轮完成」是否播放 `host.soundFile`；waiting-question / waiting-approval 不受它影响，必播 |
| `clickOpens` | `true` | 单击小球是否写 `open` 命令并置前 DSH |
| `hidePanelMs` | `600` | 鼠标离开球与面板后多少毫秒淡出，夹在 100 ~ 5000 |
| `autoStart` | `false` | 开机自启（写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `DshPetFloat` 值） |

日志：`%APPDATA%\dsh-pet\log.txt`（>1MB 自动清空重写；未捕获异常会写在这里，不会弹框崩掉）。

主动退出标记：`%APPDATA%\dsh-pet\quit.flag`

- 用户从**托盘菜单「退出」**（或任何真正结束进程的用户操作）退出时，伴侣先写这个文件再退出，内容
  `{"ts":<毫秒时间戳>,"reason":"user"}`；日志会打「检测到用户主动退出，写 quit.flag」。
- 伴侣**每次启动**都会删掉它（启动一次 = 用户又想要它了），日志打「启动：清除 quit.flag」。
- 宿主插件的看门狗读这个标记：存在 → 不自动拉起（用户手动关掉就是关掉），所以不会有「关不掉的宠物」；
  崩溃退出不写标记，异常退出照旧自动重启。
- 程序化退出（`--selftest-open` / `--selftest-quit` 之外的自动退出、关机注销）不写标记。

---

## 5. 实现要点（给后来改代码的人）

- **逐像素透明**：`UpdateLayeredWindow` + 32 位 ARGB 位图（`Native.LayeredSurface`），
  同时缓存一份 alpha 表用于命中测试：`WM_NCHITTEST` 里 alpha<12 的像素返回 `HTTRANSPARENT`，
  这就是「透明处可点穿、球体可交互」的实现方式，不需要 Region。
- **不抢焦点**：窗口样式带 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，`ShowWithoutActivation=true`，
  面板同理；需要置前的是 DSH 窗口，不是自己。
- **DPI**：启动时 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`（失败退回 `SetProcessDPIAware`），
  窗口按 `GetDpiForWindow(hwnd)/96` 得到倍率，处理 `WM_DPICHANGED`。
  所有绘制尺寸以 DIP 计算再乘倍率；**画布和窗口尺寸都夹了硬上限**（球 ≤240px、面板 ≤840×1600），
  防止 DPI 读错时在屏幕上画出巨大的球。
- **单实例**：命名互斥量 `Local\DshPetFloat`，已有实例时新进程直接退出码 0。
- **健壮性**：状态文件每 250ms 轮询（容错读、失败当离线）；文件被删/写坏/`ts` 超过 8s 都只表现成离线态，不报错、不播音；
  动画定时器 40ms；`Application.ThreadException` 与 `AppDomain.UnhandledException` 都写日志后继续。
- **面板布局与绘制共用 `PanelArt.ComputePanelLayout`**：布局（DIP 矩形）和命中测试、渲染用的是同一份几何，
  所以「点到哪个按钮」和「画在哪儿」不会错位。
- 代码按职责拆成几个小文件：`Json.cs`（手写 JSON）、`Util.cs`（路径/原子写/日志/时长格式）、
  `Native.cs`（Win32 与分层窗口）、`Model.cs`（契约模型与配置）、`PetArt.cs`（全部自绘）、
  `BallWindow.cs`（悬浮球窗口）、`PanelWindow.cs`（面板窗口）、`PetApp.cs`（业务：状态轮询、事件、声音、命令、托盘）、`Program.cs`（入口/参数/单实例/自测）。

## 6. 已知限制

- 只能开机自启到**默认状态/命令路径**：注册表里只写 exe 路径，不额外塞 `--state/--cmd` 参数。
  如果你把 DSH_HOME 改到别处，需要自己改快捷方式。
- 置前 DSH 依赖 `electronPid` 或窗口标题后缀「 — DeepSeek Harness」；找不到窗口不会崩，
  面板底部会提示「未找到 DSH 窗口」。
- 角色图（`host.avatarFile`）读不到时画渐变圆 + 「鱼」字兜底，不会报错。
- 声音走 `winmm.PlaySound`，只支持系统能解码的格式（wav 最稳）。
