// BubbleWindow.cs
// 第三窗口：小球旁边的小文本气泡（SPEC 3.5.2）。
//
// 为什么单独开一个窗口而不是画在球窗口里？
// 球窗口只有 120 DIP 见方，气泡要能到 260 DIP 宽、还能停在球上/球下，
// 塞进球窗口就得把整块画布放大，命中测试和拖动都要跟着重算；独立分层窗口最省事。
//
// 实现要点：
//  - 分层窗口（UpdateLayeredWindow）逐像素透明，圆角与尖角外全是透明像素；
//  - 用 Region 限制窗口形状，圆角外的点击会穿透到桌面，不会挡住别的东西；
//  - WS_EX_NOACTIVATE + ShowWithoutActivation：弹气泡不会把焦点从 DSH 抢走；
//  - 宽度自适应（≤260 DIP），高度按换行行数，尖角朝向小球。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DshPetFloat
{
    internal sealed class BubbleWindow : Form
    {
        private const float MaxWidthDip = 260f;
        private const float PadXDip = 11f;
        private const float PadYDip = 8f;
        private const float TailDip = 7f;
        private const float RadiusDip = 10f;

        private readonly LayeredSurface _surface = new LayeredSurface();
        private readonly Bitmap _measureBitmap;
        private readonly Graphics _measureGraphics;

        private float _configScale = 1.0f;
        private float _dpiScale = 1.0f;

        private string _text = string.Empty;
        private Size _sizePx = Size.Empty;
        private bool _tailDown = true;      // true = 气泡在球上方，尖角朝下
        private Rectangle _ballBounds = Rectangle.Empty;

        private bool _wantVisible;
        private float _alpha;
        private long _showAtMs;
        private long _hideAtMs;
        private double _lastAnimMs;
        private bool _dirty = true;
        private bool _lastShown;

        public BubbleWindow()
        {
            Text = "DSH 宠物气泡";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);

            _measureBitmap = new Bitmap(8, 8);
            _measureGraphics = Graphics.FromImage(_measureBitmap);
            _measureGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
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
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        public bool IsShown
        {
            get { return _lastShown; }
        }

        public string Text2
        {
            get { return _text; }
        }

        public void SetConfigScale(float scale)
        {
            _configScale = scale <= 0f ? 1f : scale;
            _dirty = true;
        }

        // new：隐藏 Control.Scale(float) 方法，这里表达的是“比屏幕像素与 DIP 的倍数”
        public new float Scale
        {
            get
            {
                float scale = _configScale * (_dpiScale <= 0f ? 1f : _dpiScale);
                if (float.IsNaN(scale) || scale < 0.5f) return 0.5f;
                if (scale > 3f) return 3f;
                return scale;
            }
        }

        /// <summary>显示一句台词。durationMs 到点后自动淡出（SPEC：捏捏气泡 1.6s 后淡出）。</summary>
        public void ShowLine(string text, Rectangle ballBounds, int durationMs)
        {
            if (!Util.IsVisibleText(text))
            {
                HideBubble();
                return;
            }
            try
            {
                if (durationMs <= 0)
                    durationMs = 1600;
                _text = text.Trim();
                _ballBounds = ballBounds;
                _wantVisible = true;
                _showAtMs = Util.NowMs();
                _hideAtMs = _showAtMs + durationMs;
                _dirty = true;
                UpdateGeometry();
                RenderNow();
            }
            catch (Exception ex)
            {
                Log.Error("BubbleWindow.ShowLine", ex);
            }
        }

        public void HideBubble()
        {
            if (!_wantVisible && _alpha <= 0.001f)
                return;
            _wantVisible = false;
            _dirty = true;
        }

        /// <summary>每帧（40ms）调用：跟随小球、推进淡入淡出、到点收起。</summary>
        public void Tick(double animMs, Rectangle ballBounds)
        {
            try
            {
                double dt = _lastAnimMs > 0 ? Math.Min(200.0, animMs - _lastAnimMs) : 16.0;
                if (dt < 0) dt = 0;
                _lastAnimMs = animMs;

                if (!ballBounds.IsEmpty && !ballBounds.Equals(_ballBounds))
                {
                    _ballBounds = ballBounds;
                    _dirty = true;
                }
                if (_wantVisible && _hideAtMs > 0 && Util.NowMs() >= _hideAtMs)
                    _wantVisible = false;

                float target = _wantVisible ? 1f : 0f;
                if (_alpha < target)
                    _alpha = (float)Math.Min(target, _alpha + dt / 120.0);
                else if (_alpha > target)
                    _alpha = (float)Math.Max(target, _alpha - dt / 160.0);
                if (Math.Abs(_alpha - target) > 0.002f)
                    _dirty = true;

                if (!_wantVisible && _alpha <= 0.001f)
                {
                    if (_lastShown)
                    {
                        Hide();
                        _lastShown = false;
                    }
                    return;
                }
                if (_dirty || !_lastShown)
                    RenderNow();
            }
            catch (Exception ex)
            {
                Log.Error("BubbleWindow.Tick", ex);
            }
        }

        /// <summary>按当前文字与朝向重算窗口尺寸与位置（贴球上方居中，放不下就翻到下方）。</summary>
        private void UpdateGeometry()
        {
            float scale = Scale;
            int widthPx = 0;
            int heightPx = 0;
            MeasureBubble(_text, scale, out widthPx, out heightPx);

            // 硬保险：气泡再怎么脏数据也不许超过半个屏幕（历史教训：巨球）
            int maxW = (int)Math.Round(MaxWidthDip * scale);
            if (widthPx > maxW) widthPx = maxW;
            if (widthPx < 40) widthPx = 40;
            if (heightPx > 400) heightPx = 400;
            if (heightPx < 28) heightPx = 28;

            Size size = new Size(widthPx, heightPx);
            bool sizeChanged = _sizePx != size;
            _sizePx = size;

            Rectangle work;
            Rectangle ball = _ballBounds.IsEmpty ? new Rectangle(Screen.PrimaryScreen.WorkingArea.Right - 120, Screen.PrimaryScreen.WorkingArea.Bottom - 200, 120, 120) : _ballBounds;
            work = Screen.FromRectangle(ball).WorkingArea;

            // 默认贴在球上方，尖角朝下；上面放不下就翻到下面，尖角朝上
            int gap = (int)Math.Round(3f * scale);
            int x = ball.X + ball.Width / 2 - widthPx / 2;
            int yAbove = ball.Y - heightPx + gap;
            _tailDown = yAbove >= work.Top + 2;
            int y = _tailDown ? yAbove : ball.Bottom - gap;

            if (x < work.Left + 2) x = work.Left + 2;
            if (x + widthPx > work.Right - 2) x = work.Right - 2 - widthPx;
            if (y < work.Top + 2) y = work.Top + 2;
            if (y + heightPx > work.Bottom - 2) y = work.Bottom - 2 - heightPx;

            if (Location.X != x || Location.Y != y)
                Location = new Point(x, y);
            if (sizeChanged || !_lastShown)
                _dirty = true;
        }

        private void RenderNow()
        {
            try
            {
                if (_sizePx.Width <= 0 || _sizePx.Height <= 0)
                    return;
                // v1.2.0（Lead m00777 第 2 条）：每次都先重算几何。
                // 之前只在"首次显示/尺寸变化"时算位置，窗口已显示而球被拖动时气泡就钉在原地不动了。
                // UpdateGeometry 内部只在 Location 真的变化时才赋值，成本可以忽略。
                UpdateGeometry();

                float scale = Scale;
                using (Bitmap bmp = RenderBubbleBitmap(_text, scale, _tailDown))
                {
                    // 尺寸不一致（文字在窗口已显示时变了）就重建窗口形状
                    if (bmp.Width != _sizePx.Width || bmp.Height != _sizePx.Height)
                    {
                        _sizePx = bmp.Size;
                        UpdateGeometry();
                    }
                    ApplyRegion(bmp.Width, bmp.Height, scale, _tailDown);

                    if (!_lastShown)
                    {
                        ShowWithoutActivationInternal();
                    }
                    _surface.Update(Handle, bmp, Location, _alpha);
                    _dirty = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("BubbleWindow.RenderNow", ex);
            }
        }

        private void ShowWithoutActivationInternal()
        {
            // ShowWindow(SW_SHOWNOACTIVATE) 比 Form.Show() 更"安静"，不会闪一下失焦
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            _lastShown = true;
            Native.SetTopMost(Handle);
        }

        /// <summary>用圆角矩形+尖角构成的路径设置窗口 Region：形状外的点击直接穿透。</summary>
        private void ApplyRegion(int widthPx, int heightPx, float scale, bool tailDown)
        {
            try
            {
                using (GraphicsPath path = BuildPath(new Size(widthPx, heightPx), scale, tailDown))
                {
                    Region old = Region;
                    Region = new Region(path);
                    if (old != null)
                        old.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error("BubbleWindow.ApplyRegion", ex);
            }
        }

        /// <summary>气泡整体轮廓：圆角矩形 + 三角形尖角（拼成一个路径，填充描边都整齐）。</summary>
        private static GraphicsPath BuildPath(Size size, float scale, bool tailDown)
        {
            float r = RadiusDip * scale;
            float tailH = TailDip * scale;
            float bodyH = Math.Max(8f, size.Height - tailH);
            RectangleF body = new RectangleF(0, 0, size.Width, bodyH);
            GraphicsPath path = Draw.RoundRect(body, r);
            float tipX = size.Width / 2f;
            float baseHalf = 7f * scale;
            PointF[] tail;
            if (tailDown)
            {
                tail = new PointF[]
                {
                    new PointF(tipX - baseHalf, bodyH - 1f),
                    new PointF(tipX + baseHalf, bodyH - 1f),
                    new PointF(tipX, bodyH + tailH)
                };
            }
            else
            {
                // 尖角朝上：整体路径先向下平移 tailH，再把尖角画在顶部
                Matrix shift = new Matrix();
                shift.Translate(0, tailH);
                path.Transform(shift);
                shift.Dispose();
                tail = new PointF[]
                {
                    new PointF(tipX - baseHalf, tailH + 1f),
                    new PointF(tipX + baseHalf, tailH + 1f),
                    new PointF(tipX, 0f)
                };
            }
            path.AddPolygon(tail);
            return path;
        }

        /// <summary>按文字测量气泡位图尺寸（像素）。</summary>
        private void MeasureBubble(string text, float scale, out int widthPx, out int heightPx)
        {
            float maxTextW = MaxWidthDip * scale - PadXDip * 2f * scale;
            if (maxTextW < 20f) maxTextW = 20f;
            float textW = 40f * scale;
            float textH = 14f * scale;
            try
            {
                if (Util.IsVisibleText(text))
                {
                    using (Font font = Draw.CreateFont(9.5f, FontStyle.Regular, scale))
                    using (StringFormat sf = Draw.WrapFormat())
                    {
                        SizeF measured = _measureGraphics.MeasureString(text, font, new SizeF(maxTextW, 4000f), sf);
                        textW = Math.Min(maxTextW, Math.Max(20f, measured.Width + 2f));
                        textH = Math.Max(12f * scale, measured.Height);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("BubbleWindow.MeasureBubble", ex);
            }
            widthPx = (int)Math.Ceiling(textW + PadXDip * 2f * scale);
            heightPx = (int)Math.Ceiling(textH + PadYDip * 2f * scale + TailDip * scale);
        }

        /// <summary>
        /// 画气泡位图（静态方法，selftest 可以直接调用出图，不需要真实窗口）。
        /// 深色半透明底 + 白字，尖角朝小球；圆角与尖角外是透明像素。
        /// </summary>
        public static Bitmap RenderBubbleBitmap(string text, float scale, bool tailDown)
        {
            if (scale < 0.5f) scale = 0.5f;
            if (scale > 3f) scale = 3f;
            int widthPx = 60;
            int heightPx = 28;
            try
            {
                using (Bitmap measureBmp = new Bitmap(8, 8))
                using (Graphics mg = Graphics.FromImage(measureBmp))
                using (Font font = Draw.CreateFont(9.5f, FontStyle.Regular, scale))
                using (StringFormat sf = Draw.WrapFormat())
                {
                    float maxTextW = MaxWidthDip * scale - PadXDip * 2f * scale;
                    SizeF measured = mg.MeasureString(Util.IsVisibleText(text) ? text : "气泡", font,
                        new SizeF(maxTextW, 4000f), sf);
                    float textW = Math.Min(maxTextW, Math.Max(20f, measured.Width + 2f));
                    float textH = Math.Max(12f * scale, measured.Height);
                    widthPx = (int)Math.Ceiling(textW + PadXDip * 2f * scale);
                    heightPx = (int)Math.Ceiling(textH + PadYDip * 2f * scale + TailDip * scale);
                }
            }
            catch (Exception ex)
            {
                Log.Error("BubbleWindow.RenderBubbleBitmap.Measure", ex);
            }
            if (widthPx < 40) widthPx = 40;
            if (heightPx < 28) heightPx = 28;
            if (widthPx > (int)Math.Round(MaxWidthDip * scale) + 4) widthPx = (int)Math.Round(MaxWidthDip * scale);

            Bitmap bmp = new Bitmap(widthPx, heightPx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (GraphicsPath path = BuildPath(new Size(widthPx, heightPx), scale, tailDown))
                {
                    RectangleF bounds = new RectangleF(0, 0, widthPx, Math.Max(2f, heightPx - TailDip * scale));
                    using (LinearGradientBrush brush = new LinearGradientBrush(
                               new RectangleF(bounds.X, 0, Math.Max(2f, bounds.Width), Math.Max(2f, bounds.Height)),
                               PetPalette.PanelBgB, PetPalette.PanelBgA, LinearGradientMode.Vertical))
                    {
                        g.FillPath(brush, path);
                    }
                    using (Pen border = new Pen(Color.FromArgb(120, PetPalette.PanelBorder), Math.Max(1f, 1f * scale)))
                    {
                        g.DrawPath(border, path);
                    }
                }

                float textTop = tailDown ? PadYDip * scale : PadYDip * scale + TailDip * scale;
                RectangleF textRect = new RectangleF(PadXDip * scale, textTop,
                    widthPx - PadXDip * 2f * scale, heightPx - textTop - PadYDip * scale);
                using (Font font = Draw.CreateFont(9.5f, FontStyle.Regular, scale))
                using (SolidBrush brush = new SolidBrush(PetPalette.TextPrimary))
                using (StringFormat sf = Draw.WrapFormat())
                {
                    g.DrawString(Util.IsVisibleText(text) ? text : "", font, brush, textRect, sf);
                }
            }
            return bmp;
        }

        protected override void WndProc(ref Message m)
        {
            // 点气泡 = 立刻收起（SPEC：点击立刻收起）
            if (m.Msg == Native.WM_LBUTTONUP)
            {
                HideBubble();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 气泡永远不真正“关闭”进程：只隐藏
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideBubble();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_measureGraphics != null) _measureGraphics.Dispose();
                if (_measureBitmap != null) _measureBitmap.Dispose();
                _surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
