// PanelWindow.cs
// 状态面板：悬浮球旁边那张深色圆角卡片。
//
// 同样是分层窗口（逐像素透明），所以圆角外、卡片外的像素自然透出桌面；
// 面板只响应自己的矩形区域（透明角不设 HTTRANSPARENT 也无所谓，
// 因为整块面板就是"一个控件"，点空白处不该穿透到下层，免得误触桌面图标）。
//
// 面板的布局（DIP 坐标）由 PetArt.ComputePanelLayout 统一算，
// 渲染和点击热区共用同一份 PanelLayout，保证"画在哪就能点在哪"。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace DshPetFloat
{
    internal sealed class PanelWindow : Form
    {
        private readonly LayeredSurface _surface = new LayeredSurface();
        private readonly Bitmap _measureBitmap;
        private readonly Graphics _measureGraphics;

        private float _configScale = 1.0f;
        private float _dpiScale = 1.0f;

        private PetState _state;
        private bool _online;
        private bool _soundOn = true;
        private bool _sessionListOpen;
        private string _notice = string.Empty;
        private long _noticeUntil;

        private string _hoverKey = string.Empty;
        private string _pressedKey = string.Empty;
        private string _pendingKey = string.Empty; // 关掉会话列表后 key 可能变化，用点击瞬间记录

        private bool _wantVisible;
        private float _alpha;
        private double _animMs;
        private double _lastAnimMs;
        private bool _lastShown;
        private bool _dirty = true;
        private bool _quiet;

        private PetArt.PanelLayout _layout;
        private Rectangle _ballBounds = Rectangle.Empty;
        private Rectangle _lastBallBounds = Rectangle.Empty;

        /// <summary>面板上的交互请求：open / mute / close / title / session:N。</summary>
        public Action<string> ActionRequested;

        public PanelWindow(PetApp app)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "DSH 宠物状态面板";
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            Size = new Size(1, 1);
            Visible = false;

            // 量文字用的离屏 Graphics：只建一次，避免每帧 new Bitmap 浪费
            _measureBitmap = new Bitmap(1, 1);
            _measureGraphics = Graphics.FromImage(_measureBitmap);
            _measureGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
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

        public bool IsPanelShown
        {
            get { return Visible; }
        }

        public float Alpha
        {
            get { return _alpha; }
        }

        /// <summary>底部是否还挂着一条提示（未找到窗口之类），有提示时面板不要急着收。</summary>
        public bool HasNotice
        {
            get { return !string.IsNullOrEmpty(_notice); }
        }

        public new float Scale
        {
            get { return _configScale * _dpiScale; }
        }

        public bool SoundOn
        {
            get { return _soundOn; }
            set
            {
                if (_soundOn != value)
                {
                    _soundOn = value;
                    _dirty = true;
                }
            }
        }

        public void SetConfigScale(float scale)
        {
            _configScale = scale;
            _dirty = true;
        }

        public void SetState(PetState state, bool online)
        {
            _state = state;
            _online = online;
            _dirty = true;
        }

        public void SetNotice(string text, long durationMs)
        {
            _notice = Util.TrimToEmpty(text);
            _noticeUntil = Util.NowMs() + Math.Max(800, durationMs);
            _dirty = true;
        }

        public void ShowPanel()
        {
            if (!_wantVisible)
            {
                _wantVisible = true;
                _dirty = true;
                if (!IsHandleCreated)
                {
                    IntPtr h = Handle;
                }
            }
        }

        public void HidePanel()
        {
            if (_wantVisible)
            {
                _wantVisible = false;
                _dirty = true;
            }
        }

        public bool ContainsCursor(Point screen)
        {
            if (!Visible)
                return false;
            Rectangle r = new Rectangle(Location, Size);
            return r.Contains(screen);
        }

        /// <summary>
        /// 更新球的位置，面板若正在显示就跟着挪过去。
        /// 之前只在"尺寸变化/首次显示"时才重新定位，所以拖动球时面板钉在原地不动。
        /// 移动分层窗口不会损失 ULW 的内容，所以跟随只需要改 Location，不必重绘。
        /// </summary>
        public void SetBallBounds(Rectangle bounds)
        {
            _ballBounds = bounds;
            if (!Visible || bounds.Width <= 0 || bounds.Height <= 0)
                return;
            if (bounds.Equals(_lastBallBounds))
                return;
            PositionNearBall(Width, Height);
            _lastBallBounds = bounds;
        }

        /// <summary>拖动等场景下暂停动效重绘（面板内容没变，只是跟着挪）。</summary>
        public void SetQuiet(bool quiet)
        {
            if (_quiet != quiet)
                _quiet = quiet;
        }

        public void Tick(double animMs, Rectangle ballBounds)
        {
            SetBallBounds(ballBounds);
            double dt = animMs - _lastAnimMs;
            if (dt < 0)
                dt = 0;
            if (dt > 200)
                dt = 200;
            _lastAnimMs = animMs;
            _animMs = animMs;

            float target = _wantVisible ? 1.0f : 0.0f;
            if (!string.IsNullOrEmpty(_notice) && _noticeUntil > 0 && Util.NowMs() > _noticeUntil)
            {
                _notice = string.Empty;
                _dirty = true;
            }

            if (Math.Abs(_alpha - target) > 0.001f)
            {
                // 淡入快、淡出稍慢，手感更自然
                float speed = _wantVisible ? (float)(dt / 110.0) : (float)(dt / 150.0);
                if (_alpha < target)
                    _alpha = Math.Min(target, _alpha + speed);
                else
                    _alpha = Math.Max(target, _alpha - speed);
            }

            if (!_wantVisible && _alpha <= 0.001f)
            {
                _alpha = 0;
                if (Visible)
                {
                    Hide();
                    _hoverKey = string.Empty;
                }
                _lastShown = false;
                return;
            }

            UpdateHoverKey();

            bool needRender = _dirty || Math.Abs(_alpha - target) > 0.001f || NeedsAnimation() || !_lastShown;
            if (needRender)
                RenderNow();

            _lastShown = Visible;
        }

        private bool NeedsAnimation()
        {
            // 这几个状态有进度条动效/角标脉冲，需要持续重绘；静态状态只在数据变化时重绘
            if (_quiet)
                return false;
            if (!Visible)
                return false;
            if (!_online)
                return false;
            if (_state == null)
                return false;
            string st = _state.StatusOrIdle;
            return st == "running" || st == "waiting-question" || st == "waiting-approval";
        }

        private void UpdateHoverKey()
        {
            string key = string.Empty;
            if (Visible && _layout != null)
            {
                Point cursor = Native.GetCursorPosPoint();
                Point client = PointToClient(cursor);
                if (client.X >= 0 && client.Y >= 0 && client.X < Width && client.Y < Height)
                {
                    float scale = Scale;
                    float dipX = client.X / scale;
                    float dipY = client.Y / scale;
                    key = HitKeyAt(dipX, dipY);
                }
            }
            if (!string.Equals(key, _hoverKey, StringComparison.Ordinal))
            {
                _hoverKey = key;
                _dirty = true;
            }
        }

        /// <summary>把 DIP 坐标映射成热区 key（与渲染共用 PanelLayout）。</summary>
        private string HitKeyAt(float dipX, float dipY)
        {
            PetArt.PanelLayout l = _layout;
            if (l == null)
                return string.Empty;
            PointF p = new PointF(dipX, dipY);
            if (l.BtnClose.Width > 0 && l.BtnClose.Contains(p)) return "close";
            if (l.BtnMute.Width > 0 && l.BtnMute.Contains(p)) return "mute";
            if (l.BtnOpen.Width > 0 && l.BtnOpen.Contains(p)) return "open";
            if (_sessionListOpen && l.SessionRows != null)
            {
                for (int i = 0; i < l.SessionRows.Count; i++)
                {
                    if (l.SessionRows[i].Contains(p))
                        return "session:" + i;
                }
            }
            if (l.Title.Width > 0 && l.Title.Contains(p)) return "title";
            return string.Empty;
        }

        private void RenderNow()
        {
            if (!IsHandleCreated || IsDisposed)
                return;
            _dirty = false;
            _dpiScale = Native.GetWindowDpiScale(Handle, _dpiScale);
            float scale = Scale;
            // 硬保险：同球体的理由，面板也不允许被放大到离谱尺寸
            if (scale > 2.5f)
            {
                Log.Error("面板 DPI 缩放异常(scale=" + scale + ")，已按 2.5 处理", null);
                scale = 2.5f;
            }

            PetArt.PanelArtArgs args = new PetArt.PanelArtArgs();
            args.State = _state;
            args.Online = _online;
            args.SessionListOpen = _sessionListOpen;
            args.SoundOn = _soundOn;
            args.HoverKey = _hoverKey;
            args.Notice = _notice;
            args.AnimMs = _animMs;
            args.Scale = scale;

            PetArt.PanelLayout layout = PetArt.ComputePanelLayout(args, _measureGraphics);
            _layout = layout;

            int w = (int)Math.Round(PetArt.PanelArtArgs.WidthDip * scale);
            int h = (int)Math.Round(layout.HeightDip * scale);
            if (w > 840) w = 840;   // 320 DIP * 2.5 的上限
            if (h > 1600) h = 1600;
            if (w < 80) w = 80;
            if (h < 60) h = 60;

            bool sizeChanged = (Width != w || Height != h);
            if (sizeChanged)
                Size = new Size(w, h);

            if (sizeChanged || !Visible)
                PositionNearBall(w, h);
            _lastBallBounds = _ballBounds;

            using (Bitmap bmp = PetArt.RenderPanel(args, layout))
            {
                _surface.Update(Handle, bmp, Location, _alpha);
            }

            if (!Visible && _wantVisible)
                Show();
        }

        /// <summary>面板贴着球放：优先左侧，左边放不下就翻到右侧，最后钳进屏幕。</summary>
        private void PositionNearBall(int w, int h)
        {
            Rectangle ball = _ballBounds;
            if (ball.Width <= 0 || ball.Height <= 0)
            {
                // 还没拿到球位置时先用光标所在屏右下角兜底
                Rectangle wa0 = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa0.Right - w - 20, wa0.Bottom - h - 20);
                return;
            }

            float scale = Scale;
            int gap = (int)Math.Round(10 * scale);
            Screen scr = Screen.FromPoint(new Point(ball.Left + ball.Width / 2, ball.Top + ball.Height / 2));
            Rectangle wa = scr.WorkingArea;

            int x = ball.Left - w - gap;
            if (x < wa.Left)
                x = ball.Right + gap;
            if (x + w > wa.Right)
                x = wa.Right - w;
            if (x < wa.Left)
                x = wa.Left;

            int y = ball.Top + (ball.Height - h) / 2;
            if (y + h > wa.Bottom)
                y = wa.Bottom - h;
            if (y < wa.Top)
                y = wa.Top;

            Location = new Point(x, y);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_LBUTTONDOWN:
                    {
                        int lp = m.LParam.ToInt32();
                        int cx = (short)(lp & 0xFFFF);
                        int cy = (short)((lp >> 16) & 0xFFFF);
                        float dipX = cx / Scale;
                        float dipY = cy / Scale;
                        _pendingKey = HitKeyAt(dipX, dipY);
                        _pressedKey = _pendingKey;
                        return;
                    }
                case Native.WM_LBUTTONUP:
                    {
                        int lp = m.LParam.ToInt32();
                        int cx = (short)(lp & 0xFFFF);
                        int cy = (short)((lp >> 16) & 0xFFFF);
                        float dipX = cx / Scale;
                        float dipY = cy / Scale;
                        string key = HitKeyAt(dipX, dipY);
                        string pressed = _pressedKey;
                        _pressedKey = string.Empty;
                        if (!string.IsNullOrEmpty(key) && key == pressed)
                            ActivateKey(key);
                        return;
                    }
                case Native.WM_DPICHANGED:
                    {
                        try
                        {
                            int newDpi = m.WParam.ToInt32() & 0xFFFF;
                            _dpiScale = newDpi / 96.0f;
                            _dirty = true;
                            RenderNow();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("面板处理 WM_DPICHANGED", ex);
                        }
                        m.Result = IntPtr.Zero;
                        return;
                    }
            }
            base.WndProc(ref m);
        }

        private void ActivateKey(string key)
        {
            if (key == "title")
            {
                _sessionListOpen = !_sessionListOpen;
                _dirty = true;
                return; // 展开/收起是面板内部行为，不打扰宿主
            }
            if (key == "close")
            {
                HidePanel();
            }
            if (ActionRequested != null)
                ActionRequested(key);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 面板没有"关闭"概念，一律隐藏；退出时由 PetApp 调 AllowClose + Close
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HidePanel();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _surface.Dispose();
                _measureGraphics.Dispose();
                _measureBitmap.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
