// BallWindow.cs
// 悬浮球窗口：一个 UpdateLayeredWindow 的逐像素透明窗口。
//
// 为什么不用普通 WinForms 控件 + Region：Region 只能做二值（硬边）裁剪，
// 圆球和光晕会有锯齿；而且 Region 方案在透明像素上仍然吃鼠标事件，
// 做不到"点透到下层窗口"。分层窗口（WS_EX_LAYERED + UpdateLayeredWindow）
// 天然带 per-pixel alpha，配合 WM_NCHITTEST 返回 HTTRANSPARENT 就能两全。
//
// 交互事件用"轮询 + 窗口消息"两条腿：
//   - 窗口消息（WM_LBUTTONDOWN/UP）负责正常情况下的按下/抬起；
//   - Timer 轮询（GetAsyncKeyState + 光标位置）负责鼠标移出窗口后松开、
//     拖动跟随这类消息不可靠的场景（NOACTIVATE 窗口收不到鼠标捕获是常事）。
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshPetFloat
{
    internal sealed class BallWindow : Form
    {
        private readonly PetApp _app;
        private readonly LayeredSurface _surface = new LayeredSurface();

        private float _configScale = 1.0f;   // 用户配置里的缩放
        private float _dpiScale = 1.0f;      // 当前显示器 DPI / 96
        private bool _visible;
        private bool _hover;
        private bool _dirty = true;
        private double _animMs;
        private bool _allowClose;

        // 按下/拖动状态
        private bool _pressed;
        private bool _dragging;
        private Point _pressScreen;
        private Point _pressWindow;
        private long _pressMs;
        private int _moveCount;

        // 捏捏（SPEC 3.5.3）：按下不动 600ms 内抬起 = 捏一次；按住 ≥600ms 不动 = 持续揉，每 400ms 一次
        private const int ClickMaxMs = 600;
        private const int RubHoldMs = 600;
        private const int RubIntervalMs = 400;
        private bool _rubbing;
        // 仅 --selftest-press 使用：模拟按住时鼠标键并没有真的按下，靠这个标记跳过"松开"兜底
        private bool _testHolding;
        private bool _testHoverHit;
        private long _lastRubMs;

        // 挤压回弹动画（0 表示没在播）：压扁 80ms → 过冲 120ms → 回正 160ms
        private const int SquashTotalMs = 360;
        private long _squashStartMs;
        private bool _squashHorizontal = true; // true=左右捏（x 压扁），false=上下捏
        private float _squashX = 1f;
        private float _squashY = 1f;
        private bool _squashDirty;

        // 当前展示用状态
        private string _status = "idle";
        private string _phase = "idle";
        private bool _online;
        private string _avatarPath = string.Empty;
        private Image _avatar;

        // 由 PetApp 挂接的回调
        public Action Clicked;                 // 单击（写 open 命令 + 置前 DSH）
        public Action RightClicked;            // 右键（弹菜单）
        public Action<Point> PositionCommitted; // 拖动结束（保存位置）
        public Action Moved;                   // 拖动中每次移动（让面板实时跟随）
        public Action Pet;                     // 被捏了一下（PetApp 记账、放音、弹台词）

        /// <summary>是否正在被拖动（面板据此保持显示并跟随）。</summary>
        public bool IsDragging
        {
            get { return _dragging; }
        }

        public BallWindow(PetApp app)
        {
            _app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "DSH 宠物悬浮球";
            // 分层窗口由 UpdateLayeredWindow 直接推像素，不需要 WinForms 绘制，
            // 关掉 OnPaintBackground 防止首次显示闪一下黑底。
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; } // 永远不抢前台焦点（契约要求"不抢焦点"）
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        public bool BallVisible
        {
            get { return _visible; }
        }

        public bool Hover
        {
            get { return _hover; }
        }

        /// <summary>配置缩放 × DPI 缩放（所有 DIP 尺寸乘这个值）。</summary>
        public new float Scale
        {
            get { return _configScale * _dpiScale; }
        }

        public Point ScreenOrigin
        {
            get { return Location; }
        }

        public void SetConfigScale(float scale)
        {
            _configScale = scale;
            ApplySize(true);
            _dirty = true;
        }

        public void SetState(string status, string phase, bool online)
        {
            status = PetState.NormalizeStatus(status);
            phase = PetState.NormalizePhase(phase);
            if (status == _status && phase == _phase && online == _online)
                return;
            _status = status;
            _phase = phase;
            _online = online;
            _dirty = true;
        }

        public void SetAvatar(string path)
        {
            path = Util.TrimToEmpty(path);
            if (string.Equals(path, _avatarPath, StringComparison.OrdinalIgnoreCase))
                return;
            _avatarPath = path;
            if (_avatar != null)
            {
                _avatar.Dispose();
                _avatar = null;
            }
            if (path.Length > 0)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        // 用 FileShare.ReadWrite 打开：头像文件可能正被别的进程替换，
                        // 直接用 Image.FromFile 会锁住文件并导致对方写失败。
                        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (Image src = Image.FromStream(fs))
                        {
                            _avatar = new Bitmap(src);
                        }
                    }
                    else
                    {
                        Log.Info("头像文件不存在，用内置鱼字兜底: " + path);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("加载头像失败 " + path, ex);
                }
            }
            _dirty = true;
        }

        /// <summary>首次显示前定位：优先配置里的坐标，否则贴右下角。</summary>
        public void PlaceInitial()
        {
            EnsureSized();
            int w = Width;
            int h = Height;
            float scale = Scale;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;

            int x, y;
            Config cfg = _app.Config;
            if (cfg.PosX >= 0 && cfg.PosY >= 0 && Util.IsPointOnAnyScreen(cfg.PosX + w / 2, cfg.PosY + h / 2))
            {
                x = cfg.PosX;
                y = cfg.PosY;
            }
            else
            {
                // 契约：默认距右 48 DIP、距下 96 DIP
                x = wa.Right - w - (int)Math.Round(48 * scale);
                y = wa.Bottom - h - (int)Math.Round(96 * scale);
            }
            MoveTo(x, y);
        }

        /// <summary>把窗口左上角移到屏幕坐标 (x,y)。</summary>
        public void MoveTo(int x, int y)
        {
            if (!IsHandleCreated)
            {
                Location = new Point(x, y);
                return;
            }
            // 用 WinForms 的 Location setter 走 SetWindowPos(SWP_NOACTIVATE)，
            // 这样内部 bounds 缓存和真实窗口位置始终一致（ULW 要用它当目标坐标）。
            Location = new Point(x, y);
            _dirty = true;
        }

        public void ShowBall()
        {
            IntPtr handle = Handle; // 强制创建句柄，先渲染再显示，避免首帧黑底
            ApplySize(false);
            _visible = true;
            RenderNow();
            if (!Visible)
                Show();
            Native.SetTopMost(handle);
            _dirty = true;
        }

        public void HideBall()
        {
            _visible = false;
            _hover = false;
            if (Visible)
                Hide();
        }

        /// <summary>允许真正 Close（退出流程用）。</summary>
        public void AllowClose()
        {
            _allowClose = true;
        }

        /// <summary>是否是本球的可见像素（透明像素返回 false，用于命中测试）。</summary>
        public bool HitTestScreen(Point screen)
        {
            if (!_visible || !IsHandleCreated || IsDisposed)
                return false;
            try
            {
                Point client = PointToClient(screen);
                if (client.X < 0 || client.Y < 0 || client.X >= _surface.Width || client.Y >= _surface.Height)
                    return false;
                return _surface.AlphaAt(client.X, client.Y) >= 12; // 阈值：几乎全透明的边缘算穿透
            }
            catch
            {
                return false;
            }
        }

        /// <summary>供托盘图标复用同款球体渲染。</summary>
        public Bitmap RenderBallBitmap(float scale)
        {
            BallArtArgs args = new BallArtArgs();
            args.Status = _status;
            args.Phase = _phase;
            args.Online = _online;
            args.Hover = false;
            args.AnimMs = 0;
            args.Scale = scale;
            args.Avatar = _avatar;
            return PetArt.RenderBall(args);
        }

        /// <summary>每 40ms 调一次：处理拖动/悬停/动画并重绘。</summary>
        public void Tick(double animMs)
        {
            _animMs = animMs;
            if (!_visible)
                return;

            Point cursor = Native.GetCursorPosPoint();
            bool leftDown = Native.IsKeyDown(Native.VK_LBUTTON);

            if (_pressed)
            {
                if (!_testHolding && !leftDown)
                {
                    // 鼠标在窗口外松开，收不到 WM_LBUTTONUP，靠轮询兜底
                    EndPress();
                }
                else
                {
                    UpdateDrag();
                }
            }

            bool hover = false;
            if (!_dragging)
                hover = HitTestScreen(cursor) && Native.IsTopWindowAt(cursor, Handle);
            if (hover != _hover)
            {
                _hover = hover;
                _dirty = true;
            }

            // 按住不动：到 600ms 开始持续揉，之后每 400ms 再揉一次（SPEC 3.5.3）
            if (_pressed && !_dragging)
            {
                long held = Util.NowMs() - _pressMs;
                if (held >= RubHoldMs)
                {
                    long now = Util.NowMs();
                    if (!_rubbing)
                    {
                        _rubbing = true;
                        _lastRubMs = now;
                        TriggerPet(cursor);
                    }
                    else if (now - _lastRubMs >= RubIntervalMs)
                    {
                        _lastRubMs = now;
                        TriggerPet(cursor);
                    }
                }
            }

            UpdateSquash();

            // 拖动期间不要跑动画重绘：球的位图没有变化，重绘只是白烧 GDI+（之前卡顿的另一个来源）
            if (!_dragging && (NeedsAnimation() || _squashDirty))
                _dirty = true;

            RenderIfNeeded();
        }

        /// <summary>
        /// 拖动跟随：只把窗口移到新位置，不做任何重绘。
        /// UpdateLayeredWindow 的内容是跟着窗口矩形走的，移动窗口本身就会整体平移，
        /// 所以拖动时完全不需要重新合成位图——这是之前 40ms 一跳、看着"卡"的根因。
        /// </summary>
        private void UpdateDrag()
        {
            Point cursor = Native.GetCursorPosPoint();
            int dx = cursor.X - _pressScreen.X;
            int dy = cursor.Y - _pressScreen.Y;
            if (!_dragging && (dx * dx + dy * dy) >= 16) // 4px 阈值
            {
                _dragging = true;
                Log.Info("开始拖动悬浮球");
            }
            if (!_dragging)
                return;
            MoveWindowOnly(_pressWindow.X + dx, _pressWindow.Y + dy);
        }

        /// <summary>只移动窗口，不标脏、不重绘（拖动用）。</summary>
        private void MoveWindowOnly(int x, int y)
        {
            if (Location.X == x && Location.Y == y)
                return;
            Location = new Point(x, y);
            _moveCount++;
            if (Moved != null)
                Moved();
        }

        private bool NeedsAnimation()
        {
            // running 有呼吸/旋转弧；两个 waiting 有角标脉冲；其余是静态图
            if (_status == "running" || _status == "waiting-question" || _status == "waiting-approval")
                return true;
            return _squashStartMs > 0;
        }

        /// <summary>
        /// 算当前挤压系数（1 = 没变形）。三段关键帧：压扁 80ms → 过冲到反方向 120ms → 回正 160ms。
        /// 球体按 SquashX/SquashY 做非等比缩放，角标不参与（见 PetArt.RenderBall）。
        /// </summary>
        private void UpdateSquash()
        {
            if (_squashStartMs <= 0)
                return;
            long elapsed = Util.NowMs() - _squashStartMs;
            if (elapsed < 0) elapsed = 0;
            if (elapsed >= SquashTotalMs)
            {
                _squashStartMs = 0;
                _squashX = 1f;
                _squashY = 1f;
                _squashDirty = true;
                return;
            }

            const float amp = 0.12f;
            float off; // 沿压扁轴的偏移量：正值=压扁
            if (elapsed < 80)
            {
                off = amp * (elapsed / 80f);
            }
            else if (elapsed < 200)
            {
                // 过冲：从压到最扁反弹到反方向一点点（-0.35*amp），弹性感来自这里
                float k = (elapsed - 80) / 120f;
                off = amp - (amp + amp * 0.35f) * k;
            }
            else
            {
                float k = (elapsed - 200) / 160f;
                off = -amp * 0.35f * (1f - k);
            }

            if (_squashHorizontal)
            {
                _squashX = 1f - off;
                _squashY = 1f + off * 0.6f; // 压扁时另一轴略微鼓出来，模拟体积守恒
            }
            else
            {
                _squashX = 1f + off * 0.6f;
                _squashY = 1f - off;
            }
            _squashDirty = true;
        }

        /// <summary>捏一下：按按下的位置决定压扁方向（横向捏就左右扁），然后通知 PetApp。</summary>
        private void TriggerPet(Point screenPoint)
        {
            try
            {
                Point center = new Point(Left + Width / 2, Top + Height / 2);
                _squashHorizontal = Math.Abs(screenPoint.X - center.X) >= Math.Abs(screenPoint.Y - center.Y);
                _squashStartMs = Util.NowMs();
                _squashDirty = true;
                if (Pet != null)
                    Pet();
            }
            catch (Exception ex)
            {
                Log.Error("BallWindow.TriggerPet", ex);
            }
        }

        private void RenderIfNeeded()
        {
            // 拖动中一律推迟重绘：位置变化不需要新位图，状态更新留到松手后一次画完，
            // 免得 GDI+ 在拖动路径上和鼠标抢时间。
            if (_dragging)
                return;
            if (!_dirty)
                return;
            RenderNow();
        }

        public void RenderNow()
        {
            if (!IsHandleCreated || IsDisposed)
                return;
            _dirty = false;
            _dpiScale = Native.GetWindowDpiScale(Handle, _dpiScale);
            ApplySize(true);

            BallArtArgs args = new BallArtArgs();
            args.Status = _status;
            args.Phase = _phase;
            args.Online = _online;
            args.Hover = _hover;
            args.AnimMs = _animMs;
            args.Scale = Scale;
            args.Avatar = _avatar;
            args.SquashX = _squashX;
            args.SquashY = _squashY;

            _squashDirty = false;
            using (Bitmap bmp = PetArt.RenderBall(args))
            {
                _surface.Update(Handle, bmp, Location, 1.0f);
            }
        }

        private void EnsureSized()
        {
            if (IsHandleCreated)
                _dpiScale = Native.GetWindowDpiScale(Handle, _dpiScale);
            ApplySize(false);
        }

        /// <summary>按 Scale 重算窗口边长；keepCenter 时保持球心不动。</summary>
        private void ApplySize(bool keepCenter)
        {
            if (!IsHandleCreated || IsDisposed)
                return;
            float scale = Scale;
            // 硬保险：DPI 单位一旦读错（例如把 96 当成倍率），120 DIP 会变成 1 万多像素，
            // 直接铺满整个屏幕。这里无论如何都不允许球体超过 240px。
            if (scale > 2.0f)
            {
                Log.Error("球体 DPI 缩放异常(scale=" + scale + ")，已按 2.0 处理", null);
                scale = 2.0f;
            }
            int size = (int)Math.Round(BallArtArgs.CanvasDip * scale);
            if (size > 400)
                Log.Error("球体边长 " + size + "px 异常，已夹到 240px", null);
            if (size > 240)
                size = 240;
            if (size < 24)
                size = 24;
            if (Width == size && Height == size)
                return;

            Point center = new Point(Left + Width / 2, Top + Height / 2);
            Size = new Size(size, size);
            if (keepCenter && Width > 0 && Height > 0)
                Location = new Point(center.X - size / 2, center.Y - size / 2);
            _dirty = true;
        }

        private void BeginPress()
        {
            Point cursor = Native.GetCursorPosPoint();
            if (!HitTestScreen(cursor) || !Native.IsTopWindowAt(cursor, Handle))
                return;
            _pressed = true;
            _dragging = false;
            _moveCount = 0;
            _rubbing = false;
            _lastRubMs = 0;
            _pressScreen = cursor;
            _pressWindow = Location;
            _pressMs = Util.NowMs();
            Native.SetCapture(Handle);
        }

        private void EndPress()
        {
            if (!_pressed)
                return;
            _pressed = false;
            Native.ReleaseCapture();
            bool wasDrag = _dragging;
            _dragging = false;
            _dirty = true;

            if (wasDrag)
            {
                Log.Info("拖动结束，位置 " + Location.X + "," + Location.Y + "（跟随 " + _moveCount + " 次）");
                if (PositionCommitted != null)
                    PositionCommitted(Location);
                return;
            }

            long elapsed = Util.NowMs() - _pressMs;
            bool rubbed = _rubbing;
            _rubbing = false;

            // 揉过（按住 ≥600ms）松手：只算捏，绝不触发 open —— 不然想捏就会跳窗口
            if (rubbed)
            {
                Log.Info("揉捏结束（按住 " + elapsed + "ms）");
                return;
            }

            Point cursor = Native.GetCursorPosPoint();
            if (!_testHoverHit && !HitTestScreen(cursor))
                return;

            // v1.1.1（Lead m00728）：<600ms 的短按 = 只 open（安静：不计数、不出声、不弹台词、不压扁）；
            // 捏只由 ≥600ms 的按住揉触发，日常点球切回对话不会被"啵"一声打断。
            if (elapsed <= ClickMaxMs)
            {
                if (Clicked != null)
                    Clicked();
            }
            else
            {
                // 理论上到不了这里（≥600ms 时 Tick 已经进入揉捏分支），留个日志便于排查
                Log.Info("长按 " + elapsed + "ms 松手：未进入揉捏，忽略");
            }
        }

        // ───────── v1.1.1 自测钩子（只有 --selftest-press 会调，正常运行路径不碰） ─────────

        /// <summary>模拟按下不放：把 _pressMs 设为现在，之后 Tick 会按真实节奏进入揉捏。</summary>
        public void TestBeginHold()
        {
            Point cursor = Native.GetCursorPosPoint();
            _testHolding = true;
            _pressed = true;
            _dragging = false;
            _moveCount = 0;
            _rubbing = false;
            _lastRubMs = 0;
            _pressScreen = cursor;
            _pressWindow = Location;
            _pressMs = Util.NowMs();
        }

        /// <summary>结束模拟按住：走真实的 EndPress 判定（揉过就只算捏，不发 open）。</summary>
        public void TestEndHold()
        {
            _testHolding = false;
            _testHoverHit = true;
            EndPress();
            _testHoverHit = false;
        }

        /// <summary>模拟一次 heldMs 的短按：用于验证"短按不捏、只 open"。</summary>
        public void TestShortClick(long heldMs)
        {
            TestBeginHold();
            _testHolding = false;
            _pressMs = Util.NowMs() - heldMs;
            TestEndHold();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 契约：关闭主窗口 = 隐藏到托盘，不退出进程
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideBall();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_NCHITTEST:
                    {
                        base.WndProc(ref m);
                        if (m.Result.ToInt32() == Native.HTCLIENT)
                        {
                            // 用消息里的屏幕坐标判断（比 GetCursorPos 更准确）
                            int lp = m.LParam.ToInt32();
                            Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                            if (!HitTestScreen(screen))
                                m.Result = (IntPtr)Native.HTTRANSPARENT; // 透明像素：点击穿透到下层
                        }
                        return;
                    }
                case Native.WM_MOUSEMOVE:
                    // 拖动时不要等到下一个 tick（40ms），鼠标一动就跟着走，手感才跟手
                    if (_pressed)
                    {
                        UpdateDrag();
                        return;
                    }
                    break;
                case Native.WM_LBUTTONDOWN:
                    BeginPress();
                    return; // 吞掉，不让 WinForms 激活窗口
                case Native.WM_LBUTTONUP:
                    EndPress();
                    return;
                case Native.WM_RBUTTONUP:
                    {
                        int lp = m.LParam.ToInt32();
                        Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                        if (HitTestScreen(screen))
                        {
                            if (RightClicked != null)
                                RightClicked();
                        }
                        return;
                    }
                case Native.WM_DPICHANGED:
                    HandleDpiChanged(m);
                    return;
            }
            base.WndProc(ref m);
        }

        /// <summary>跨屏拖动时 DPI 变了：按新 DPI 重算尺寸，位置用系统建议矩形。</summary>
        private void HandleDpiChanged(Message m)
        {
            try
            {
                int newDpi = m.WParam.ToInt32() & 0xFFFF;
                _dpiScale = newDpi / 96.0f;
                Native.RECT rc = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                ApplySize(true);
                if (rc.Right > rc.Left && rc.Bottom > rc.Top)
                    Location = new Point(rc.Left, rc.Top);
                _dirty = true;
                RenderNow();
            }
            catch (Exception ex)
            {
                Log.Error("处理 WM_DPICHANGED", ex);
            }
            m.Result = IntPtr.Zero;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_avatar != null)
                {
                    _avatar.Dispose();
                    _avatar = null;
                }
                _surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
