// PetApp.cs
// 伴侣进程的大脑：轮询状态文件、驱动悬浮球/面板、处理点击与声音、托盘与配置。
//
// 线程模型：全部 UI 操作都在主线程（两个 WinForms Timer，一个 250ms 读状态，
// 一个 40ms 做动画与交互），没有跨线程访问控件的麻烦，也就不用 Invoke 满地跑。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshPetFloat
{
    /// <summary>启动参数解析后的结果（Program.cs 填）。</summary>
    internal sealed class PetOptions
    {
        public string StatePath = Paths.DefaultStateFile;
        public string CmdPath = Paths.DefaultCmdFile;
        public bool Demo;
        public bool CmdWritable = true;
        public bool SaveConfig = true;
        public bool SelftestOpen;
        public bool SelftestQuit;
        public string SelftestRenderDir;
    }

    internal sealed class PetApp : ApplicationContext
    {
        private const string AutoStartValueName = "DshPetFloat";
        private const int StatePollMs = 250;   // 契约：250ms 轮询
        private const int TickMs = 40;         // 25fps，够动画用又不烧 CPU
        private const long OfflineAfterMs = 8000; // 契约：ts 超过 8 秒没更新算离线

        private readonly PetOptions _opt;
        private Config _config;
        private PetState _state = new PetState();
        private bool _online;
        private long _lastPanelSeq = -1;
        private bool _eventsSeeded;
        private readonly HashSet<string> _seenEvents = new HashSet<string>(StringComparer.Ordinal);

        private readonly Timer _stateTimer = new Timer();
        private readonly Timer _tickTimer = new Timer();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _animMs;

        private BallWindow _ball;
        private PanelWindow _panel;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _menuMute;
        private ToolStripMenuItem _menuAuto;
        private ToolStripMenuItem _menuHide;
        private Icon _trayIcon;
        private IntPtr _trayIconHandle = IntPtr.Zero;
        private string _trayKey = "";

        private long _cmdSeq = 1;
        private bool _panelSuppressed;  // 点了面板的 ×：光标离开之前别再弹
        private long _lastInteractionMs;
        private bool _exiting;

        public PetApp(PetOptions opt)
        {
            _opt = opt;
            if (opt.Demo && !File.Exists(Paths.ConfigFile))
                _config = new Config(); // 演示模式不污染真实配置：没有就内存里用默认值
            else
                _config = Config.Load(Paths.ConfigFile);

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        }

        public Config Config
        {
            get { return _config; }
        }

        public void Start()
        {
            Log.Info("伴侣启动：demo=" + _opt.Demo + " state=" + _opt.StatePath + " cmd=" + _opt.CmdPath);
            // 启动即视为"用户又想要它了"：清掉上次手动退出留下的标记，否则宿主看门狗会一直不拉起。
            if (ClearQuitFlag())
                Log.Info("启动：清除 quit.flag");
            _cmdSeq = ReadExistingCmdSeq();

            _ball = new BallWindow(this);
            _ball.SetConfigScale(_config.Scale);
            _ball.PlaceInitial();
            _ball.Clicked = OnBallClicked;
            _ball.RightClicked = OnBallRightClick;
            _ball.PositionCommitted = OnBallPositionCommitted;
            _ball.Moved = OnBallMoved;
            _ball.ShowBall();

            _panel = new PanelWindow(this);
            _panel.SetConfigScale(_config.Scale);
            _panel.SoundOn = _config.SoundOn;
            _panel.ActionRequested = OnPanelAction;
            _panel.SetState(_state, false);

            SyncAutoStart();
            CreateTray();

            _stateTimer.Interval = StatePollMs;
            _stateTimer.Tick += OnStateTimerTick;
            _stateTimer.Start();

            _tickTimer.Interval = TickMs;
            _tickTimer.Tick += OnTickTimerTick;
            _tickTimer.Start();

            Log.Info("timers tickEnabled=" + _tickTimer.Enabled + " tickInterval=" + _tickTimer.Interval
                     + " stateEnabled=" + _stateTimer.Enabled + " stateInterval=" + _stateTimer.Interval);

            ReadState(); // 别等第一个 250ms，立刻读一次

            if (_opt.SelftestOpen)
                StartSelftestOpen();
            if (_opt.SelftestQuit)
                StartSelftestQuit();
        }

        // ───────────────────── 状态文件 ─────────────────────

        private void OnStateTimerTick(object sender, EventArgs e)
        {
            ReadState();
        }

        private void ReadState()
        {
            if (_exiting)
                return;
            try
            {
                string text = Util.ReadTextShared(_opt.StatePath);
                if (string.IsNullOrEmpty(text))
                {
                    MarkOffline("状态文件不可读");
                    return;
                }
                JsonValue root;
                if (!JsonParser.TryParse(text, out root) || root == null || !root.IsObject)
                {
                    MarkOffline("状态文件不是合法 JSON");
                    return;
                }

                PetState st = PetState.Parse(root);
                // 契约：ts 超过 8 秒没更新 = 宿主没在跑（或卡死），伴侣转离线姿态。
                // 演示模式的演示文件是静态快照，ts 不会自己流逝，因此只看 ts 是否存在。
                bool online = st.Ts > 0;
                if (online && !_opt.Demo)
                    online = (Util.NowMs() - st.Ts) <= OfflineAfterMs;
                st.Online = online;

                if (online != _online)
                    Log.Info("在线状态切换 → " + (online ? "在线" : "离线"));
                _online = online;

                bool seqChanged = st.Seq != _lastPanelSeq || st.Online != _state.Online;
                _state = st;

                _ball.SetAvatar(st.Host != null ? st.Host.AvatarFile : "");
                _ball.SetState(st.StatusOrIdle, st.Active != null ? st.Active.Phase : "idle", online);
                if (seqChanged || !_panel.IsPanelShown)
                {
                    _panel.SetState(st, online);
                    _lastPanelSeq = st.Seq;
                }

                HandleEvents(st);
            }
            catch (Exception ex)
            {
                Log.Error("ReadState", ex);
            }
        }

        private void MarkOffline(string reason)
        {
            if (!_online && _state.Online == false)
                return;
            bool wasOnline = _online;
            _online = false;
            _state.Online = false;
            _ball.SetState("idle", "idle", false);
            _panel.SetState(_state, false);
            if (wasOnline)
                Log.Info("离线：" + reason);
        }

        /// <summary>事件去重：冷启动把已有事件全部记为"已见"，不补播（契约）。</summary>
        private void HandleEvents(PetState st)
        {
            if (!_eventsSeeded)
            {
                for (int i = 0; i < st.Events.Count; i++)
                {
                    if (!string.IsNullOrEmpty(st.Events[i].Id))
                        _seenEvents.Add(st.Events[i].Id);
                }
                _eventsSeeded = true;
                Log.Info("冷启动：已见事件 " + _seenEvents.Count + " 条，不补播");
                return;
            }

            for (int i = 0; i < st.Events.Count; i++)
            {
                PetEventInfo ev = st.Events[i];
                if (ev == null || string.IsNullOrEmpty(ev.Id))
                    continue;
                if (_seenEvents.Contains(ev.Id))
                    continue;
                _seenEvents.Add(ev.Id);
                PlayEventSound(ev.Kind);
            }

            // HashSet 无序也没法淘汰，索性整体重来一遍（事件最多 20 条，代价可忽略）
            if (_seenEvents.Count > 400)
            {
                _seenEvents.Clear();
                for (int i = 0; i < st.Events.Count; i++)
                {
                    if (!string.IsNullOrEmpty(st.Events[i].Id))
                        _seenEvents.Add(st.Events[i].Id);
                }
            }
        }

        private void PlayEventSound(string kind)
        {
            if (!_config.SoundOn)
                return;
            if (kind == "turn-done" && !_config.EndSoundOn)
                return; // 契约：turn-done 可配置是否播音
            string path = _state.Host != null ? _state.Host.SoundFile : "";
            if (!Util.IsVisibleText(path) || !File.Exists(path))
            {
                Log.Info("没有可用提示音，跳过事件 " + kind);
                return;
            }
            Native.PlayWav(path);
            Log.Info("播放提示音：" + kind);
        }

        // ───────────────────── 定时动画 / 面板显隐 ─────────────────────

        private void OnTickTimerTick(object sender, EventArgs e)
        {
            if (_exiting)
                return;
            try
            {
                _animMs = _clock.Elapsed.TotalMilliseconds;
                _ball.Tick(_animMs);

                Rectangle ballBounds = new Rectangle(_ball.Location, _ball.Size);
                _panel.SetQuiet(_ball.IsDragging);
                _panel.Tick(_animMs, ballBounds);

                Point cursor = Native.GetCursorPosPoint();
                // 光标贴着球（含拖动刚结束的一瞬）就算“在球上”：分层窗口偶发拿不到 Hover 时不至于把面板收掉
                bool nearBall = _ball.Hover || _ball.IsDragging || PointNear(ballBounds, cursor, 24);
                bool overBall = _ball.BallVisible && nearBall;
                bool overPanel = _panel.ContainsCursor(cursor);

                if (!overBall && !overPanel)
                    _panelSuppressed = false;

                bool needPanel = ((overBall || overPanel) && !_panelSuppressed) || _panel.HasNotice;
                if (needPanel)
                {
                    _lastInteractionMs = Util.NowMs();
                    _panel.ShowPanel();
                }
                else if (_panel.IsPanelShown && Util.NowMs() - _lastInteractionMs > _config.HidePanelMs)
                {
                    _panel.HidePanel();
                }

                UpdateTrayIconIfNeeded();
            }
            catch (Exception ex)
            {
                Log.Error("Tick", ex);
            }
        }

        // ───────────────────── 悬浮球 / 面板交互 ─────────────────────

        private void OnBallClicked()
        {
            _panelSuppressed = false;
            _lastInteractionMs = Util.NowMs();
            _panel.ShowPanel();

            // 契约：单击 = 写 open 命令 + 置前 DSH；clickOpens=false 时只弹面板
            if (!_config.ClickOpens)
                return;
            string title = _state.Active != null ? _state.Active.Title : "";
            WriteCommand("open", title);
            BringDshToFront();
        }

        private void OnBallRightClick()
        {
            if (_menu == null)
                return;
            RefreshMenuState();
            try
            {
                _menu.Show(Cursor.Position);
            }
            catch (Exception ex)
            {
                Log.Error("弹出菜单", ex);
            }
        }

        private void OnBallPositionCommitted(Point p)
        {
            _config.PosX = p.X;
            _config.PosY = p.Y;
            SaveConfig();
        }

        private void OnBallMoved()
        {
            // 拖动时面板实时跟随（只改 Location，不重绘）
            _panel.SetBallBounds(new Rectangle(_ball.Location, _ball.Size));
            _lastInteractionMs = Util.NowMs();
        }

        private static bool PointNear(Rectangle r, Point p, int pad)
        {
            return p.X >= r.Left - pad && p.X <= r.Right + pad
                && p.Y >= r.Top - pad && p.Y <= r.Bottom + pad;
        }

        private void OnPanelAction(string key)
        {
            _lastInteractionMs = Util.NowMs();
            if (key == "open")
            {
                string title = _state.Active != null ? _state.Active.Title : "";
                WriteCommand("open", title);
                BringDshToFront();
                return;
            }
            if (key == "mute")
            {
                ToggleMute();
                return;
            }
            if (key == "close")
            {
                // × 只隐藏面板：抑制到光标离开球/面板为止，免得立刻又弹出来
                _panelSuppressed = true;
                _panel.HidePanel();
                return;
            }
            if (key.StartsWith("session:", StringComparison.Ordinal))
            {
                int idx;
                if (!int.TryParse(key.Substring(8), out idx))
                    return;
                if (_state.Sessions == null || idx < 0 || idx >= _state.Sessions.Count)
                    return;
                string title = _state.Sessions[idx].Title;
                WriteCommand("open", title);
                BringDshToFront();
            }
        }

        // ───────────────────── 命令文件 ─────────────────────

        private long ReadExistingCmdSeq()
        {
            long seq = 0;
            try
            {
                string text = Util.ReadTextShared(_opt.CmdPath);
                JsonValue root;
                if (!string.IsNullOrEmpty(text) && JsonParser.TryParse(text, out root)
                    && root != null && root.IsObject)
                {
                    seq = root.Long("id", 0);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取已有命令 id", ex);
            }
            if (seq < 0)
                seq = 0;
            return seq + 1;
        }

        /// <summary>写命令文件（宿主轮询这个文件来置前/切会话）。</summary>
        private void WriteCommand(string action, string title)
        {
            if (!_opt.CmdWritable)
            {
                Log.Info("演示模式，不写命令: " + action + " title=" + (title ?? ""));
                return;
            }
            try
            {
                long id = _cmdSeq++;
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"id\": ").Append(id.ToString()).Append(",\r\n");
                sb.Append("  \"action\": ").Append(JsonText.Quote(action ?? "open")).Append(",\r\n");
                sb.Append("  \"title\": ").Append(JsonText.Quote(title ?? "")).Append(",\r\n");
                sb.Append("  \"ts\": ").Append(Util.NowMs().ToString()).Append("\r\n");
                sb.Append("}\r\n");

                if (Util.AtomicWrite(_opt.CmdPath, sb.ToString()))
                    Log.Info("写命令 id=" + id + " action=" + action + " title=" + (title ?? ""));
                else
                    Log.Info("命令写盘失败: " + _opt.CmdPath);
            }
            catch (Exception ex)
            {
                Log.Error("WriteCommand", ex);
            }
        }

        // ───────────────────── 置前 DSH ─────────────────────

        private void BringDshToFront()
        {
            try
            {
                IntPtr hwnd = IntPtr.Zero;
                HostInfo host = _state.Host;
                if (host != null && host.ElectronPid > 0)
                    hwnd = Native.FindVisibleWindowByPid(host.ElectronPid);
                if (hwnd == IntPtr.Zero && host != null && host.Pid > 0)
                    hwnd = Native.FindVisibleWindowByPid(host.Pid);
                if (hwnd == IntPtr.Zero)
                    hwnd = Native.FindWindowByTitleSuffix(" — DeepSeek Harness");
                if (hwnd == IntPtr.Zero)
                    hwnd = Native.FindWindowByTitleSuffix("- DeepSeek Harness");

                if (hwnd == IntPtr.Zero)
                {
                    Log.Info("未找到 DSH 窗口（宿主可能没启动）");
                    ShowNotice("未找到 DSH 窗口", 3000);
                    return;
                }

                bool foreground = Native.BringWindowToFront(hwnd);
                if (!foreground)
                    Log.Info("SetForegroundWindow 被系统拒绝，已用 SetWindowPos 兜底置顶");
            }
            catch (Exception ex)
            {
                Log.Error("BringDshToFront", ex);
            }
        }

        private void ShowNotice(string text, int durationMs)
        {
            _panelSuppressed = false;
            _panel.SetNotice(text, durationMs);
            _panel.ShowPanel();
            _lastInteractionMs = Util.NowMs();
        }

        // ───────────────────── 静音 / 自启 / 显示隐藏 ─────────────────────

        private void ToggleMute()
        {
            _config.SoundOn = !_config.SoundOn;
            _panel.SoundOn = _config.SoundOn;
            SaveConfig();
            RefreshMenuState();
            Log.Info("静音 = " + (!_config.SoundOn));
        }

        private void SyncAutoStart()
        {
            if (_opt.Demo)
                return;
            // 启动时同步：先只读查询当前值，只有真的不一致时才去写注册表。
            // 否则每次启动都 OpenSubKey(writable) 一次，在受限环境里会刷一堆
            // SecurityException 噪音（实测在沙箱里就是这样），也可能被杀软拦。
            string current = ReadAutoStartValue();
            if (_config.AutoStart)
            {
                string want = "\"" + Paths.ExePath + "\"";
                if (!string.Equals(current, want, StringComparison.OrdinalIgnoreCase))
                    SetAutoStart(true, false);
            }
            else if (current != null)
            {
                SetAutoStart(false, false); // 清理可能残留的旧路径（比如 exe 换过位置）
            }
        }

        private static string ReadAutoStartValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null)
                        return null;
                    object v = key.GetValue(AutoStartValueName);
                    return v == null ? null : v.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Error("ReadAutoStartValue", ex);
                return null;
            }
        }

        private bool SetAutoStart(bool on, bool notify)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null)
                    {
                        if (notify)
                            ShowNotice("无法写入注册表，自启设置失败", 2500);
                        return false;
                    }
                    if (on)
                    {
                        // 只写 exe 路径：状态/命令文件都用默认位置，自启时也能正常工作
                        key.SetValue(AutoStartValueName, "\"" + Paths.ExePath + "\"");
                        Log.Info("已设置开机自启：" + Paths.ExePath);
                    }
                    else if (key.GetValue(AutoStartValueName) != null)
                    {
                        key.DeleteValue(AutoStartValueName, false);
                        Log.Info("已取消开机自启");
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("SetAutoStart", ex);
                if (notify)
                    ShowNotice("设置开机自启失败", 2500);
                return false;
            }
        }

        private void ToggleAutoStart()
        {
            bool target = !_config.AutoStart;
            if (SetAutoStart(target, true))
            {
                _config.AutoStart = target;
                SaveConfig();
            }
            RefreshMenuState();
        }

        private void ToggleBallVisible()
        {
            if (_ball.BallVisible)
            {
                _ball.HideBall();
                _panel.HidePanel();
                Log.Info("隐藏悬浮球");
            }
            else
            {
                _ball.ShowBall();
                Log.Info("显示悬浮球");
            }
            RefreshMenuState();
        }

        // ───────────────────── 托盘与菜单 ─────────────────────

        private void CreateTray()
        {
            _menu = BuildMenu();
            _tray = new NotifyIcon();
            _tray.Text = "DSH 宠物悬浮球";
            UpdateTrayIconIfNeeded();
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += OnTrayMouseClick;
            _tray.Visible = true;
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            ToolStripMenuItem open = new ToolStripMenuItem("打开 DSH");
            open.Click += delegate { WriteCommand("open", _state.Active != null ? _state.Active.Title : ""); BringDshToFront(); };
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());

            _menuMute = new ToolStripMenuItem(_config.SoundOn ? "静音" : "取消静音");
            _menuMute.Click += delegate { ToggleMute(); };
            menu.Items.Add(_menuMute);

            _menuAuto = new ToolStripMenuItem("开机自启");
            _menuAuto.Checked = _config.AutoStart;
            _menuAuto.Click += delegate { ToggleAutoStart(); };
            menu.Items.Add(_menuAuto);

            _menuHide = new ToolStripMenuItem("隐藏小球");
            _menuHide.Click += delegate { ToggleBallVisible(); };
            menu.Items.Add(_menuHide);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            // 用户主动退出：ExitApp(true) 会先写 quit.flag，宿主看门狗见标记就不再复活
            quit.Click += delegate { ExitApp(true); };
            menu.Items.Add(quit);
            return menu;
        }

        private void RefreshMenuState()
        {
            if (_menuMute != null)
                _menuMute.Text = _config.SoundOn ? "静音" : "取消静音";
            if (_menuAuto != null)
                _menuAuto.Checked = _config.AutoStart;
            if (_menuHide != null)
                _menuHide.Text = _ball != null && _ball.BallVisible ? "隐藏小球" : "显示小球";
        }

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                ToggleBallVisible(); // 契约：托盘左键切换显示
        }

        private void UpdateTrayIconIfNeeded()
        {
            if (_tray == null)
                return;
            string key = (_online ? _state.StatusOrIdle : "offline");
            if (key == _trayKey && _trayIcon != null)
                return;
            _trayKey = key;
            try
            {
                // 复用球体渲染：托盘图标和小球状态颜色永远一致
                using (Bitmap bmp = _ball.RenderBallBitmap(96f / 120f))
                {
                    IntPtr newHandle = bmp.GetHicon();
                    Icon newIcon = Icon.FromHandle(newHandle);
                    Icon oldIcon = _trayIcon;
                    IntPtr oldHandle = _trayIconHandle;
                    _trayIcon = newIcon;
                    _trayIconHandle = newHandle;
                    _tray.Icon = newIcon;
                    if (oldIcon != null)
                        oldIcon.Dispose();
                    if (oldHandle != IntPtr.Zero)
                        Native.DestroyIcon(oldHandle);
                }
            }
            catch (Exception ex)
            {
                Log.Error("更新托盘图标", ex);
            }
        }

        // ───────────────────── 自测 / 退出 ─────────────────────

        /// <summary>隐藏开关：启动后写一条 open 命令再退出，用来验证命令文件格式。</summary>
        private void StartSelftestOpen()
        {
            Log.Info("自测：1.2 秒后写一条 open 命令");
            Timer t = new Timer();
            t.Interval = 1200;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                WriteCommand("open", _state.Active != null ? _state.Active.Title : "自测会话");
                Timer t2 = new Timer();
                t2.Interval = 600;
                t2.Tick += delegate
                {
                    t2.Stop();
                    t2.Dispose();
                    Log.Info("自测：退出");
                    ExitApp(false);
                };
                t2.Start();
            };
            t.Start();
        }

        /// <summary>隐藏开关：1.2 秒后走一次"用户主动退出"路径，用来验证 quit.flag。</summary>
        private void StartSelftestQuit()
        {
            Log.Info("自测：1.2 秒后按用户主动退出处理");
            Timer t = new Timer();
            t.Interval = 1200;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                ExitApp(true);
            };
            t.Start();
        }

        /// <summary>
        /// 写"用户主动退出"标记。宿主插件的看门狗靠它区分"用户不想看了"和"进程崩了"：
        /// 有这个文件就不自动拉起（详见 SPEC 之外的 companion/README.md 说明）。
        /// 只写最小的 JSON，方便插件直接读；写失败只记日志，绝不能因此卡住退出。
        /// </summary>
        private void WriteQuitFlag()
        {
            try
            {
                string text = "{\r\n  \"ts\": " + Util.NowMs() + ",\r\n  \"reason\": \"user\"\r\n}\r\n";
                if (Util.AtomicWrite(Paths.QuitFlagFile, text))
                    Log.Info("检测到用户主动退出，写 quit.flag");
                else
                    Log.Info("检测到用户主动退出，但写 quit.flag 失败");
            }
            catch (Exception ex)
            {
                Log.Error("写 quit.flag", ex);
            }
        }

        /// <summary>启动时清除标记，返回"原本是否存在"（用来决定要不要打日志）。</summary>
        private static bool ClearQuitFlag()
        {
            try
            {
                if (!File.Exists(Paths.QuitFlagFile))
                    return false;
                File.Delete(Paths.QuitFlagFile);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("清除 quit.flag", ex);
                return false;
            }
        }

        private void SaveConfig()
        {
            if (!_opt.SaveConfig)
                return;
            try
            {
                _config.Save(Paths.ConfigFile);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置", ex);
            }
        }

        public void ExitApp(bool userInitiated)
        {
            if (_exiting)
                return;
            _exiting = true;
            // 先写标记再收摊：反过来的话进程可能在写盘前就没了，看门狗会立刻把它拉回来。
            if (userInitiated)
                WriteQuitFlag();
            Log.Info("伴侣退出");
            try
            {
                _stateTimer.Stop();
                _tickTimer.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("停止定时器", ex);
            }
            SaveConfig();

            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("销毁托盘", ex);
            }

            try
            {
                if (_panel != null)
                    _panel.Close();
                if (_ball != null)
                {
                    _ball.AllowClose();
                    _ball.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Error("关闭窗口", ex);
            }

            ExitThread();
        }

        private void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Log.Error("UI 线程未处理异常", e.Exception);
        }

        private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error("AppDomain 未处理异常", e.ExceptionObject as Exception);
        }
    }
}
