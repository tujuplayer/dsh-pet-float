// PetArt.cs
// 全部离线绘制：悬浮球 + 状态面板。没有 XAML、没有图片资源依赖，
// 这样单文件 exe 到哪台机器都能跑，不透明区域由 alpha 天然决定。
// 所有尺寸先按 DIP 设计，绘制时乘 scale（DPI * 用户配置缩放）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace DshPetFloat
{
    internal static class PetPalette
    {
        public static readonly Color BallGradientA = Color.FromArgb(255, 91, 124, 250);
        public static readonly Color BallGradientB = Color.FromArgb(255, 155, 92, 255);
        public static readonly Color TextPrimary = Color.FromArgb(255, 242, 244, 255);
        public static readonly Color TextSecondary = Color.FromArgb(255, 168, 175, 207);
        public static readonly Color TextDim = Color.FromArgb(255, 122, 130, 166);
        public static readonly Color PanelBgA = Color.FromArgb(244, 36, 30, 68);
        public static readonly Color PanelBgB = Color.FromArgb(244, 22, 20, 43);
        public static readonly Color PanelBorder = Color.FromArgb(48, 255, 255, 255);
        public static readonly Color TrackBg = Color.FromArgb(40, 255, 255, 255);
        public static readonly Color BtnBg = Color.FromArgb(26, 255, 255, 255);
        public static readonly Color BtnBgHover = Color.FromArgb(54, 255, 255, 255);
        public static readonly Color SessionBg = Color.FromArgb(16, 255, 255, 255);
        public static readonly Color SessionBgHover = Color.FromArgb(38, 255, 255, 255);
        public static readonly Color NoticeColor = Color.FromArgb(255, 255, 190, 120);
        public static readonly Color OfflineRing = Color.FromArgb(255, 130, 136, 150);

        public static Color StatusColor(string status)
        {
            switch (status)
            {
                case "running": return Color.FromArgb(255, 66, 133, 244);       // 蓝：思考中
                case "waiting-question": return Color.FromArgb(255, 245, 158, 11); // 橙：等你回答
                case "waiting-approval": return Color.FromArgb(255, 239, 68, 68);  // 红：等你授权
                case "done": return Color.FromArgb(255, 34, 197, 94);           // 绿：已完成
                case "error": return Color.FromArgb(255, 190, 30, 30);          // 深红：出错
                default: return Color.FromArgb(255, 138, 143, 152);              // 灰：空闲
            }
        }

        public static Color StatusColorBright(string status)
        {
            Color c = StatusColor(status);
            return Color.FromArgb(255,
                Math.Min(255, c.R + 60),
                Math.Min(255, c.G + 60),
                Math.Min(255, c.B + 60));
        }

        /// <summary>面板胶囊与状态环共用的中文文案（契约给的 7 种状态文案）。</summary>
        public static string StatusText(string status, string phase)
        {
            switch (status)
            {
                case "running": return phase == "tool" ? "调用工具" : "思考中";
                case "waiting-question": return "等你回答";
                case "waiting-approval": return "等你授权";
                case "done": return "已完成";
                case "error": return "出错";
                default: return "空闲";
            }
        }
    }

    internal static class Draw
    {
        public static Font CreateFont(float pt, FontStyle style, float scale)
        {
            // GDI+ 的 Point 单位依赖 Graphics.DpiY，直接用 Pixel 单位避免双重缩放
            float px = pt * 96f / 72f * scale;
            if (px < 1f) px = 1f;
            return new Font("Microsoft YaHei UI", px, style, GraphicsUnit.Pixel);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRoundRect(Graphics g, Brush brush, RectangleF r, float radius)
        {
            using (GraphicsPath path = RoundRect(r, radius))
                g.FillPath(brush, path);
        }

        public static void DrawRoundRect(Graphics g, Pen pen, RectangleF r, float radius)
        {
            using (GraphicsPath path = RoundRect(r, radius))
                g.DrawPath(pen, path);
        }

        public static StringFormat Format(ContentAlignment align, bool ellipsis)
        {
            // 不能用 GenericTypographic：它走字体的排版行高，中文字体行高常大于布局给出的行高，
            // 文字会被裁在矩形外而整行消失（实测 12.5pt 粗体 20px 高时整行不渲染）。
            // GenericDefault 的行高更贴合“一行一个字高”，配合 NoWrap 做单行省略最稳。
            StringFormat sf = new StringFormat();
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            if (ellipsis)
                sf.Trimming = StringTrimming.EllipsisCharacter;
            switch (align)
            {
                case ContentAlignment.MiddleLeft:
                case ContentAlignment.TopLeft:
                case ContentAlignment.BottomLeft:
                    sf.Alignment = StringAlignment.Near;
                    break;
                case ContentAlignment.MiddleRight:
                case ContentAlignment.TopRight:
                case ContentAlignment.BottomRight:
                    sf.Alignment = StringAlignment.Far;
                    break;
                default:
                    sf.Alignment = StringAlignment.Center;
                    break;
            }
            switch (align)
            {
                case ContentAlignment.TopLeft:
                case ContentAlignment.TopCenter:
                case ContentAlignment.TopRight:
                    sf.LineAlignment = StringAlignment.Near;
                    break;
                case ContentAlignment.BottomLeft:
                case ContentAlignment.BottomCenter:
                case ContentAlignment.BottomRight:
                    sf.LineAlignment = StringAlignment.Far;
                    break;
                default:
                    sf.LineAlignment = StringAlignment.Center;
                    break;
            }
            return sf;
        }

        public static StringFormat WrapFormat()
        {
            // 必须用 GenericDefault（允许换行），GenericTypographic 是 NoWrap 的
            StringFormat sf = new StringFormat();
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Near;
            return sf;
        }

        /// <summary>整图 alpha 乘系数（离线半透明用）。</summary>
        public static void MultiplyAlpha(Bitmap bmp, float factor)
        {
            try
            {
                if (factor >= 0.999f)
                    return;
                if (factor < 0f) factor = 0f;
                Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    int rowBytes = bmp.Width * 4;
                    byte[] row = new byte[rowBytes];
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        IntPtr rowPtr = new IntPtr(data.Scan0.ToInt64() + (long)y * stride);
                        System.Runtime.InteropServices.Marshal.Copy(rowPtr, row, 0, rowBytes);
                        for (int x = 0; x < rowBytes; x += 4)
                        {
                            row[x + 3] = (byte)(row[x + 3] * factor);
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, rowPtr, rowBytes);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            catch (Exception)
            {
                // 视觉降级无所谓，不能让绘制异常冒出去
            }
        }
    }

    /// <summary>悬浮球绘制参数。</summary>
    internal sealed class BallArtArgs
    {
        public string Status = "idle";
        public string Phase = "idle";
        public bool Online = true;
        public bool Hover = false;
        public double AnimMs = 0;
        public float Scale = 1f;
        public Image Avatar;
        // 捏捏（SPEC 3.5.3）：1 = 原样；压扁时一个方向 <1、另一个方向 >1
        public float SquashX = 1f;
        public float SquashY = 1f;

        public const float CanvasDip = 120f;
        public const float BallDip = 96f;
        public const float RingDip = 5f;
    }

    internal static class PetArt
    {
        // 头像缩放缓存：原图 1000+px，每帧用 HighQualityBicubic 缩到 ~100px 是拖动卡顿的主因。
        // 这里只按目标直径缓存一份 cover 位图（呼吸/悬停带来的 1~2px 抖动直接复用），
        // 每帧退化成一次小图 DrawImage，25fps 和拖动都能扛住。
        private static Image _avatarCacheSource;
        private static Bitmap _avatarCacheBmp;
        private static int _avatarCacheSize;

        /// <summary>把头像按 cover 规则缩成铺满圆形的正方形位图（带缓存）。</summary>
        private static Image AvatarCover(Image avatar, float target)
        {
            if (avatar == null || avatar.Width <= 0 || avatar.Height <= 0)
                return null;
            int d = Math.Max(8, (int)Math.Round(target));
            if (!ReferenceEquals(_avatarCacheSource, avatar) || _avatarCacheBmp == null
                || Math.Abs(d - _avatarCacheSize) > 8)
            {
                if (_avatarCacheBmp != null)
                {
                    _avatarCacheBmp.Dispose();
                    _avatarCacheBmp = null;
                }
                float s = Math.Max((float)d / avatar.Width, (float)d / avatar.Height);
                int w = Math.Max(1, (int)Math.Round(avatar.Width * s));
                int h = Math.Max(1, (int)Math.Round(avatar.Height * s));
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.DrawImage(avatar, new Rectangle(0, 0, w, h));
                }
                _avatarCacheBmp = bmp;
                _avatarCacheSize = d;
                _avatarCacheSource = avatar;
            }
            return _avatarCacheBmp;
        }

        /// <summary>
        /// 画悬浮球位图（尺寸 = 120 DIP * scale 的正方形，透明底）。
        /// 球体先按原比例画好，再整体做非等比缩放实现"被捏扁"；角标/发丝不参与缩放。
        /// </summary>
        public static Bitmap RenderBall(BallArtArgs a)
        {
            int size = Math.Max(16, (int)Math.Round(BallArtArgs.CanvasDip * a.Scale));
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            float sx = ClampSquash(a.SquashX);
            float sy = ClampSquash(a.SquashY);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                using (Bitmap core = RenderBallCore(a))
                {
                    // 以画布中心（球心）为基准缩放：球心不动，视觉上就是被手指按了一下
                    float w = size * sx;
                    float h = size * sy;
                    g.DrawImage(core, new RectangleF((size - w) * 0.5f, (size - h) * 0.5f, w, h));
                }
                DrawBallBadges(g, a, size);
            }
            // 离线整体半透明放最后，避免角标被单独压暗两次
            if (!a.Online)
                Draw.MultiplyAlpha(bmp, 0.5f);
            return bmp;
        }

        /// <summary>挤压系数夹紧：坏值当 1，绝不把球画成一条线（捏捏动画的过冲也不会超界）。</summary>
        private static float ClampSquash(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 1f;
            if (value < 0.7f) return 0.7f;
            if (value > 1.3f) return 1.3f;
            return value;
        }

        /// <summary>画球体本体（发光、状态环、头像或"鱼"字兜底、高光、内描边）。</summary>
        private static Bitmap RenderBallCore(BallArtArgs a)
        {
            int size = Math.Max(16, (int)Math.Round(BallArtArgs.CanvasDip * a.Scale));
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                float center = size / 2f;
                float hoverScale = a.Hover ? 1.06f : 1.0f;
                string status = a.Online ? a.Status : "offline";
                Color ring = a.Online ? PetPalette.StatusColor(a.Status) : PetPalette.OfflineRing;

                // 呼吸：running 时轻微缩放，动画相位由外部 25fps 计时器推进
                float breath = 1f;
                double breathValue = 0;
                if (a.Online && a.Status == "running")
                {
                    breathValue = Math.Sin(a.AnimMs / 620.0 * Math.PI * 2.0);
                    breath = 1f + (float)breathValue * 0.014f;
                }

                float radius = BallArtArgs.BallDip * a.Scale * 0.5f * hoverScale * breath;
                float ringWidth = BallArtArgs.RingDip * a.Scale * hoverScale;
                float innerRadius = radius - ringWidth * 0.5f;

                // 外发光：几层递减 alpha 的圈，比真实高斯模糊便宜且够用
                int glowLayers = a.Online && (a.Status == "running" || a.Status == "waiting-question" || a.Status == "waiting-approval") ? 5 : 3;
                for (int i = glowLayers; i >= 1; i--)
                {
                    float gr = radius + ringWidth * 0.5f + i * 1.6f * a.Scale;
                    int alpha = (int)(26 * (glowLayers - i + 1) / (float)glowLayers);
                    if (a.Status == "waiting-question" || a.Status == "waiting-approval")
                        alpha = (int)(alpha * (0.7 + 0.3 * Math.Sin(a.AnimMs / 380.0 * Math.PI * 2.0)));
                    using (Pen pen = new Pen(Color.FromArgb(Math.Max(0, Math.Min(150, alpha)), ring), Math.Max(1f, 2.2f * a.Scale)))
                    {
                        g.DrawEllipse(pen, center - gr, center - gr, gr * 2f, gr * 2f);
                    }
                }

                // 轨道底色（半透明深色，让环形更立体）
                using (Pen basePen = new Pen(Color.FromArgb(160, ring), ringWidth * 0.55f))
                {
                    g.DrawEllipse(basePen, center - radius, center - radius, radius * 2f, radius * 2f);
                }

                if (a.Online && a.Status == "running")
                {
                    // 旋转弧：视觉上表达"正在转"
                    float startAngle = (float)((a.AnimMs / 14.0) % 360.0);
                    using (Pen arcPen = new Pen(Color.FromArgb(255, PetPalette.StatusColorBright("running")), ringWidth))
                    {
                        arcPen.StartCap = LineCap.Round;
                        arcPen.EndCap = LineCap.Round;
                        g.DrawArc(arcPen, center - radius, center - radius, radius * 2f, radius * 2f,
                            startAngle, 250f);
                    }
                }
                else
                {
                    float pulse = 1f;
                    if (a.Status == "waiting-question" || a.Status == "waiting-approval")
                        pulse = 1f + 0.06f * (float)Math.Sin(a.AnimMs / 380.0 * Math.PI * 2.0);
                    using (Pen ringPen = new Pen(ring, ringWidth * pulse))
                    {
                        g.DrawEllipse(ringPen, center - radius, center - radius, radius * 2f, radius * 2f);
                    }
                }

                // 球体内部
                using (GraphicsPath innerPath = new GraphicsPath())
                {
                    innerPath.AddEllipse(center - innerRadius, center - innerRadius, innerRadius * 2f, innerRadius * 2f);
                    GraphicsState clipState = g.Save();
                    g.SetClip(innerPath, CombineMode.Intersect);
                    try
                    {
                        Image cover = AvatarCover(a.Avatar, innerRadius * 2f);
                        if (cover != null)
                        {
                            // cover 填充：缩放结果已缓存，这里只是把小图贴到球心（可接受 1~2px 抖动）
                            float w = cover.Width;
                            float h = cover.Height;
                            g.DrawImage(cover, new RectangleF(center - w / 2f, center - h / 2f, w, h));
                            using (SolidBrush veil = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
                                g.FillEllipse(veil, center - innerRadius, center - innerRadius, innerRadius * 2f, innerRadius * 2f);
                        }
                        else
                        {
                            using (LinearGradientBrush brush = new LinearGradientBrush(
                                       new RectangleF(center - innerRadius, center - innerRadius, innerRadius * 2f, innerRadius * 2f),
                                       PetPalette.BallGradientA, PetPalette.BallGradientB, 55f))
                            {
                                g.FillEllipse(brush, center - innerRadius, center - innerRadius, innerRadius * 2f, innerRadius * 2f);
                            }
                            // 兜底角色："鱼"字（没有头像文件时也得有个可爱的东西）
                            float fontPx = innerRadius * 0.95f;
                            using (Font font = new Font("Microsoft YaHei UI", Math.Max(8f, fontPx), FontStyle.Bold, GraphicsUnit.Pixel))
                            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
                            using (StringFormat sf = Draw.Format(ContentAlignment.MiddleCenter, false))
                            {
                                sf.FormatFlags |= StringFormatFlags.NoClip;
                                g.DrawString("鱼", font, textBrush,
                                    new RectangleF(center - innerRadius, center - innerRadius * 0.94f, innerRadius * 2f, innerRadius * 2f), sf);
                            }
                        }

                        // 高光，让球看起来是圆的
                        using (SolidBrush gloss = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                        {
                            float gx = center - innerRadius * 0.42f;
                            float gy = center - innerRadius * 0.62f;
                            float gw = innerRadius * 0.9f;
                            float gh = innerRadius * 0.55f;
                            g.FillEllipse(gloss, gx, gy, gw, gh);
                        }
                    }
                    finally
                    {
                        g.Restore(clipState);
                    }
                }

                // 内描边
                using (Pen innerPen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, a.Scale)))
                {
                    g.DrawEllipse(innerPen, center - innerRadius + 1f, center - innerRadius + 1f,
                        innerRadius * 2f - 2f, innerRadius * 2f - 2f);
                }

            }
            return bmp;
        }

        /// <summary>
        /// 角标单独画在压扁之后的画布上：「?」等你回答 / 「!」等你授权 / done 小对勾。
        /// SPEC 3.5.3 要求发丝与角标不随捏捏变形，所以不能画在 RenderBallCore 里。
        /// </summary>
        private static void DrawBallBadges(Graphics g, BallArtArgs a, float size)
        {
            if (!a.Online)
                return;
            if (a.Status != "waiting-question" && a.Status != "waiting-approval" && a.Status != "done")
                return;

            float center = size / 2f;
            float hoverScale = a.Hover ? 1.06f : 1.0f;
            float radius = BallArtArgs.BallDip * a.Scale * 0.5f * hoverScale;
            Color ring = PetPalette.StatusColor(a.Status);

            if (a.Status == "waiting-question" || a.Status == "waiting-approval")
            {
                string mark = a.Status == "waiting-question" ? "?" : "!";
                float pulse = 1f + 0.10f * (float)Math.Sin(a.AnimMs / 300.0 * Math.PI * 2.0);
                float badgeR = 13f * a.Scale * hoverScale * pulse;
                float bx = center + radius * 0.70f;
                float by = center - radius * 0.70f;
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillEllipse(shadow, bx - badgeR + 1.5f * a.Scale, by - badgeR + 1.5f * a.Scale, badgeR * 2f, badgeR * 2f);
                using (SolidBrush badge = new SolidBrush(ring))
                    g.FillEllipse(badge, bx - badgeR, by - badgeR, badgeR * 2f, badgeR * 2f);
                using (Pen border = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1f, 1.6f * a.Scale)))
                    g.DrawEllipse(border, bx - badgeR, by - badgeR, badgeR * 2f, badgeR * 2f);
                using (Font font = new Font("Microsoft YaHei UI", Math.Max(9f, 16f * a.Scale * hoverScale), FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleCenter, false))
                {
                    g.DrawString(mark, font, textBrush, new RectangleF(bx - badgeR, by - badgeR, badgeR * 2f, badgeR * 2f), sf);
                }
                return;
            }

            // done 状态加一个小对勾角标，和 running 的旋转弧区分开
            float doneR = 12f * a.Scale * hoverScale;
            float dx = center + radius * 0.70f;
            float dy = center - radius * 0.70f;
            using (SolidBrush badge = new SolidBrush(ring))
                g.FillEllipse(badge, dx - doneR, dy - doneR, doneR * 2f, doneR * 2f);
            using (Pen pen = new Pen(Color.White, Math.Max(1.6f, 2.2f * a.Scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                PointF p0 = new PointF(dx - doneR * 0.42f, dy + doneR * 0.02f);
                PointF p1 = new PointF(dx - doneR * 0.1f, dy + doneR * 0.36f);
                PointF p2 = new PointF(dx + doneR * 0.46f, dy - doneR * 0.34f);
                g.DrawLines(pen, new PointF[] { p0, p1, p2 });
            }
        }

        /// <summary>状态面板绘制参数（同时用于布局命中测试）。</summary>
        internal sealed class PanelArtArgs
        {
            public PetState State;
            public bool Online;
            public bool SessionListOpen;
            public bool SoundOn = true;
            public string HoverKey = "";
            public string Notice = "";
            // v1.1 钱包行文本（SPEC 3.5.4），由 PanelWindow 用 WalletFormat.PanelLine 算好
            public string WalletText = "";
            public double AnimMs;
            public float Scale = 1f;

            public const float WidthDip = 320f;

            public ActiveInfo Active
            {
                get { return State == null ? null : State.Active; }
            }

            public string Status
            {
                get { return State == null ? "idle" : State.StatusOrIdle; }
            }
        }

        internal sealed class PanelLayout
        {
            public float WidthDip = PanelArtArgs.WidthDip;
            public float HeightDip;

            public RectangleF Title;
            public RectangleF Chevron;
            public RectangleF Capsule;
            public RectangleF Thinking;
            public RectangleF Detail;
            public RectangleF QuestionBar;
            public RectangleF QuestionText;
            public RectangleF Options;
            public RectangleF ProgressTrack;
            public RectangleF Elapsed;
            public RectangleF Wallet;
            public RectangleF FooterText;
            public RectangleF BtnOpen;
            public RectangleF BtnMute;
            public RectangleF BtnClose;
            public List<RectangleF> SessionRows = new List<RectangleF>();
            public bool HasQuestion;
            public bool HasApproval;
        }

        /// <summary>
        /// 计算面板布局（DIP）。渲染与点击命中都用这一份，避免"看到的位置点不中"。
        /// 需要 Graphics 来测量文本换行，因此调用时必须给一个有效 Graphics。
        /// </summary>
        public static PanelLayout ComputePanelLayout(PanelArtArgs a, Graphics g)
        {
            const float padX = 14f;
            PanelLayout l = new PanelLayout();
            float width = PanelArtArgs.WidthDip;
            float contentW = width - padX * 2f;
            float y = 13f;

            float capsuleW = 66f;
            float capsuleH = 20f;
            l.Capsule = new RectangleF(width - padX - capsuleW, y - 1f, capsuleW, capsuleH);
            l.Title = new RectangleF(padX, y, contentW - capsuleW - 12f, 20f);
            l.Chevron = new RectangleF(l.Title.Right - 2f, y + 3f, 14f, 14f);
            y += 26f;

            l.Thinking = new RectangleF(padX, y, contentW, 15f);
            y += 17f;
            l.Detail = new RectangleF(padX, y, contentW, 16f);
            y += 22f;

            ActiveInfo active = a.Active;
            if (active != null && active.Question != null && Util.IsVisibleText(active.Question.Text))
            {
                l.HasQuestion = true;
                int lines = MeasureLines(g, active.Question.Text, 11.5f, FontStyle.Bold, a.Scale,
                    (contentW - 12f) * a.Scale, 3);
                float qh = 9f + lines * 19f + (active.Question.Options.Count > 0 ? 24f : 0f) + 8f;
                l.QuestionBar = new RectangleF(padX, y, 3f, qh);
                l.QuestionText = new RectangleF(padX + 11f, y + 8f, contentW - 12f, lines * 19f);
                if (active.Question.Options.Count > 0)
                    l.Options = new RectangleF(padX + 11f, y + 8f + lines * 19f + 2f, contentW - 12f, 20f);
                y += qh + 9f;
            }
            else if (active != null && active.Approval != null && Util.IsVisibleText(active.Approval.Text))
            {
                l.HasApproval = true;
                int lines = MeasureLines(g, active.Approval.Text, 11.5f, FontStyle.Bold, a.Scale,
                    (contentW - 12f) * a.Scale, 3);
                float qh = 9f + lines * 19f + 8f;
                l.QuestionBar = new RectangleF(padX, y, 3f, qh);
                l.QuestionText = new RectangleF(padX + 11f, y + 8f, contentW - 12f, lines * 19f);
                y += qh + 9f;
            }
            else
            {
                l.QuestionBar = RectangleF.Empty;
                l.QuestionText = RectangleF.Empty;
                l.Options = RectangleF.Empty;
            }

            float barW = contentW - 70f;
            l.ProgressTrack = new RectangleF(padX, y, barW, 7f);
            l.Elapsed = new RectangleF(width - padX - 64f, y - 3f, 64f, 14f);
            y += 7f + 13f;

            // v1.1 钱包行（SPEC 3.5.4）：进度条与底部小字之间，一行 11px 次要色
            l.Wallet = new RectangleF(padX, y + 1f, contentW, 14f);
            y += 15f;

            // 按钮收窄一点，把宽度让给底部那行「第 N 轮 · 第 M 步 · K tokens」，
            // 否则 320 DIP 下这行会被省略号吃掉 tokens 字样。
            l.BtnClose = new RectangleF(width - padX - 18f, y, 18f, 20f);
            l.BtnMute = new RectangleF(l.BtnClose.X - 5f - 40f, y, 40f, 20f);
            l.BtnOpen = new RectangleF(l.BtnMute.X - 5f - 40f, y, 40f, 20f);
            l.FooterText = new RectangleF(padX, y + 2f, Math.Max(10f, l.BtnOpen.X - padX - 8f), 16f);
            y += 20f + 11f;

            if (a.SessionListOpen)
            {
                int max = Math.Min(6, a.State == null ? 0 : a.State.Sessions.Count);
                for (int i = 0; i < max; i++)
                {
                    l.SessionRows.Add(new RectangleF(padX, y, contentW, 24f));
                    y += 26f;
                }
                if (max > 0)
                    y += 2f;
            }

            l.HeightDip = (float)Math.Ceiling(y);
            return l;
        }

        private static int MeasureLines(Graphics g, string text, float pt, FontStyle style, float scale, float maxWidthPx, int maxLines)
        {
            try
            {
                using (Font font = Draw.CreateFont(pt, style, scale))
                using (StringFormat sf = Draw.WrapFormat())
                {
                    SizeF measured = g.MeasureString(text, font, new SizeF(maxWidthPx, 10000f), sf);
                    float lineH = font.GetHeight(g);
                    if (lineH <= 0.1f)
                        lineH = 1f;
                    int lines = (int)Math.Ceiling(measured.Height / lineH - 0.15f);
                    if (lines < 1) lines = 1;
                    if (lines > maxLines) lines = maxLines;
                    return lines;
                }
            }
            catch (Exception)
            {
                return 1;
            }
        }

        /// <summary>画状态面板位图（尺寸 = 320 DIP x HeightDip，再乘 scale）。</summary>
        public static Bitmap RenderPanel(PanelArtArgs a, PanelLayout layout)
        {
            int widthPx = Math.Max(80, (int)Math.Round(layout.WidthDip * a.Scale));
            int heightPx = Math.Max(40, (int)Math.Round(layout.HeightDip * a.Scale));
            Bitmap bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppArgb);
            float s = a.Scale;

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                float radius = 14f * s;
                RectangleF bg = new RectangleF(0.5f * s, 0.5f * s, widthPx - 1f * s, heightPx - 1f * s);

                using (GraphicsPath bgPath = Draw.RoundRect(bg, radius))
                using (LinearGradientBrush bgBrush = new LinearGradientBrush(
                           new RectangleF(0, 0, widthPx, heightPx),
                           PetPalette.PanelBgA, PetPalette.PanelBgB, 90f))
                {
                    g.FillPath(bgBrush, bgPath);
                }
                using (Pen border = new Pen(PetPalette.PanelBorder, Math.Max(1f, s)))
                {
                    Draw.DrawRoundRect(g, border, bg, radius);
                }

                PetState st = a.State;
                ActiveInfo active = a.Active;
                string status = a.Online ? a.Status : "idle";
                Color statusColor = PetPalette.StatusColor(status);

                // 1) 标题 + 状态胶囊
                string title = st != null && st.Active != null && Util.IsVisibleText(st.Active.Title)
                    ? st.Active.Title
                    : (st != null && st.Page != null && Util.IsVisibleText(st.Page.Title) ? st.Page.Title : "");
                if (!a.Online)
                    title = "DSH 未连接";
                if (!Util.IsVisibleText(title))
                    title = "（无活动会话）";

                bool titleHover = a.HoverKey == "title";
                using (Font titleFont = Draw.CreateFont(12.5f, FontStyle.Bold, s))
                using (SolidBrush titleBrush = new SolidBrush(titleHover ? Color.FromArgb(255, 255, 255, 255) : PetPalette.TextPrimary))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleLeft, true))
                {
                    RectangleF tr = Scaled(layout.Title, s);
                    g.DrawString(title, titleFont, titleBrush, tr, sf);
                }
                // 展开小箭头
                using (Font chevronFont = Draw.CreateFont(8f, FontStyle.Regular, s))
                using (SolidBrush chevronBrush = new SolidBrush(PetPalette.TextDim))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleCenter, false))
                {
                    g.DrawString(a.SessionListOpen ? "▲" : "▼", chevronFont, chevronBrush,
                        Scaled(layout.Chevron, s), sf);
                }

                string capsuleText = a.Online ? PetPalette.StatusText(status, active == null ? "idle" : active.Phase) : "离线";
                Color capsuleBase = a.Online ? statusColor : PetPalette.OfflineRing;
                RectangleF capsule = Scaled(layout.Capsule, s);
                using (SolidBrush capsuleBg = new SolidBrush(Color.FromArgb(52, capsuleBase)))
                {
                    Draw.FillRoundRect(g, capsuleBg, capsule, capsule.Height / 2f);
                }
                using (Pen capsuleBorder = new Pen(Color.FromArgb(110, capsuleBase), Math.Max(1f, s)))
                {
                    Draw.DrawRoundRect(g, capsuleBorder, capsule, capsule.Height / 2f);
                }
                using (Font capsuleFont = Draw.CreateFont(8.5f, FontStyle.Bold, s))
                using (SolidBrush capsuleBrush = new SolidBrush(a.Online ? PetPalette.StatusColorBright(status) : PetPalette.TextSecondary))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleCenter, false))
                {
                    g.DrawString(capsuleText, capsuleFont, capsuleBrush, capsule, sf);
                }

                // 2) 思考摘要 + 详情
                string thinking = active == null ? "" : active.Thinking;
                if (a.Online && !Util.IsVisibleText(thinking))
                    thinking = "—";
                using (Font smallFont = Draw.CreateFont(9f, FontStyle.Regular, s))
                using (SolidBrush secondary = new SolidBrush(PetPalette.TextSecondary))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleLeft, true))
                {
                    g.DrawString(thinking, smallFont, secondary, Scaled(layout.Thinking, s), sf);
                }

                string detail = active == null ? "" : active.Detail;
                if (!a.Online)
                    detail = "等待 DSH 宿主启动…";
                using (Font detailFont = Draw.CreateFont(9.5f, FontStyle.Regular, s))
                using (SolidBrush detailBrush = new SolidBrush(PetPalette.TextPrimary))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleLeft, true))
                {
                    g.DrawString(detail, detailFont, detailBrush, Scaled(layout.Detail, s), sf);
                }

                // 3) 问题/授权区：优先大字
                if (layout.HasQuestion || layout.HasApproval)
                {
                    bool question = layout.HasQuestion;
                    Color accent = question ? PetPalette.StatusColor("waiting-question") : PetPalette.StatusColor("waiting-approval");
                    string qtext = question ? active.Question.Text : active.Approval.Text;
                    RectangleF bar = Scaled(layout.QuestionBar, s);
                    using (SolidBrush barBrush = new SolidBrush(accent))
                    {
                        Draw.FillRoundRect(g, barBrush, bar, bar.Width / 2f);
                    }
                    using (Font qFont = Draw.CreateFont(11.5f, FontStyle.Bold, s))
                    using (SolidBrush qBrush = new SolidBrush(Color.FromArgb(255, 255, 245, 235)))
                    using (StringFormat sf = Draw.WrapFormat())
                    {
                        g.DrawString(qtext, qFont, qBrush, Scaled(layout.QuestionText, s), sf);
                    }
                    if (layout.HasQuestion && active.Question.Options.Count > 0)
                    {
                        DrawOptionChips(g, active.Question.Options, Scaled(layout.Options, s), s, accent);
                    }
                }

                // 4) 进度条 + 耗时
                RectangleF track = Scaled(layout.ProgressTrack, s);
                using (SolidBrush trackBrush = new SolidBrush(PetPalette.TrackBg))
                {
                    Draw.FillRoundRect(g, trackBrush, track, track.Height / 2f);
                }

                if (active != null && active.Todos != null && active.Todos.Total > 0)
                {
                    int total = active.Todos.Total;
                    int done = Math.Max(0, Math.Min(total, active.Todos.Done));
                    int segments = Math.Min(total, 12);
                    float gap = 3f * s;
                    float segW = (track.Width - gap * (segments - 1)) / segments;
                    int filledSegments = (int)Math.Round(done / (double)total * segments);
                    for (int i = 0; i < segments; i++)
                    {
                        RectangleF seg = new RectangleF(track.X + i * (segW + gap), track.Y, segW, track.Height);
                        using (SolidBrush b = new SolidBrush(i < filledSegments ? statusColor : PetPalette.TrackBg))
                            Draw.FillRoundRect(g, b, seg, seg.Height / 2f);
                    }
                }
                else if (a.Online && (status == "running" || status == "waiting-question" || status == "waiting-approval"))
                {
                    // 不确定进度：一段高光在轨道里来回扫
                    double phase = (a.AnimMs / 1500.0) % 1.0;
                    float sweep = track.Width * 0.32f;
                    float pos = (float)(phase * (track.Width + sweep)) - sweep;
                    RectangleF view = new RectangleF(track.X, track.Y, track.Width, track.Height);
                    GraphicsState clipState = g.Save();
                    using (GraphicsPath round = Draw.RoundRect(view, track.Height / 2f))
                    {
                        g.SetClip(round, CombineMode.Intersect);
                        RectangleF bar = new RectangleF(track.X + pos, track.Y, sweep, track.Height);
                        using (LinearGradientBrush glow = new LinearGradientBrush(
                                   new RectangleF(bar.X, bar.Y, Math.Max(2f, bar.Width), bar.Height),
                                   Color.FromArgb(0, statusColor), statusColor, LinearGradientMode.Horizontal))
                        {
                            g.FillRectangle(glow, bar);
                        }
                    }
                    g.Restore(clipState);
                }

                string elapsed = active == null ? "--" : Util.FormatDuration(active.EffectiveElapsedMs());
                using (Font elapsedFont = Draw.CreateFont(9f, FontStyle.Regular, s))
                using (SolidBrush elapsedBrush = new SolidBrush(PetPalette.TextSecondary))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleRight, false))
                {
                    g.DrawString(elapsed, elapsedFont, elapsedBrush, Scaled(layout.Elapsed, s), sf);
                }

                // 4.5) 钱包行（v1.1）：取不到余额时这一行不画内容但保留行高，布局不跳动
                if (Util.IsVisibleText(a.WalletText) && layout.Wallet.Height > 0f)
                {
                    using (Font walletFont = Draw.CreateFont(8.25f, FontStyle.Regular, s))
                    using (SolidBrush walletBrush = new SolidBrush(PetPalette.TextSecondary))
                    using (StringFormat sf = Draw.Format(ContentAlignment.MiddleLeft, true))
                    {
                        g.DrawString(a.WalletText, walletFont, walletBrush, Scaled(layout.Wallet, s), sf);
                    }
                }

                // 5) 底部信息 + 按钮
                string footer;
                if (active == null)
                    footer = "—";
                else
                {
                    long tokens = active.Tokens != null ? active.Tokens.Turn : 0;
                    footer = "第 " + active.Turn.ToString() + " 轮 · 第 " + active.Step.ToString()
                             + " 步 · " + tokens.ToString() + " tokens";
                }
                if (!a.Online)
                    footer = st != null ? "上次更新 " + Util.FormatDuration(Math.Max(0, Util.NowMs() - st.Ts)) + " 前" : "等待连接";
                Color footerColor = PetPalette.TextDim;
                if (Util.IsVisibleText(a.Notice))
                {
                    footer = a.Notice;
                    footerColor = PetPalette.NoticeColor;
                }
                using (Font footerFont = Draw.CreateFont(8.5f, FontStyle.Regular, s))
                using (SolidBrush footerBrush = new SolidBrush(footerColor))
                using (StringFormat sf = Draw.Format(ContentAlignment.MiddleLeft, true))
                {
                    g.DrawString(footer, footerFont, footerBrush, Scaled(layout.FooterText, s), sf);
                }

                DrawButton(g, Scaled(layout.BtnOpen, s), "打开", a.HoverKey == "open", s, false);
                DrawButton(g, Scaled(layout.BtnMute, s), a.SoundOn ? "静音" : "已静音", a.HoverKey == "mute", s, false);
                DrawButton(g, Scaled(layout.BtnClose, s), "×", a.HoverKey == "close", s, true);

                // 6) 会话列表（点标题展开）
                if (a.SessionListOpen && st != null)
                {
                    for (int i = 0; i < layout.SessionRows.Count && i < st.Sessions.Count; i++)
                    {
                        SessionInfo session = st.Sessions[i];
                        RectangleF row = Scaled(layout.SessionRows[i], s);
                        bool hover = a.HoverKey == "session:" + i.ToString();
                        using (SolidBrush rowBg = new SolidBrush(hover ? PetPalette.SessionBgHover : PetPalette.SessionBg))
                            Draw.FillRoundRect(g, rowBg, row, 6f * s);
                        Color rowStatus = PetPalette.StatusColor(session.Status);
                        if (session.Current)
                        {
                            using (SolidBrush currentBg = new SolidBrush(Color.FromArgb(34, rowStatus)))
                                Draw.FillRoundRect(g, currentBg, row, 6f * s);
                        }
                        float dotR = 3f * s;
                        using (SolidBrush dot = new SolidBrush(rowStatus))
                            g.FillEllipse(dot, row.X + 8f * s - dotR, row.Y + row.Height / 2f - dotR, dotR * 2f, dotR * 2f);
                        string rowTitle = Util.IsVisibleText(session.Title) ? session.Title : session.Key;
                        using (Font rowFont = Draw.CreateFont(9f, session.Current ? FontStyle.Bold : FontStyle.Regular, s))
                        using (SolidBrush rowBrush = new SolidBrush(session.Current ? PetPalette.TextPrimary : PetPalette.TextSecondary))
                        using (StringFormat sf = Draw.Format(ContentAlignment.MiddleLeft, true))
                        {
                            RectangleF textRect = new RectangleF(row.X + 18f * s, row.Y, row.Width - 26f * s, row.Height);
                            g.DrawString(rowTitle, rowFont, rowBrush, textRect, sf);
                        }
                    }
                }
            }
            return bmp;
        }

        private static void DrawOptionChips(Graphics g, List<string> options, RectangleF area, float s, Color accent)
        {
            float x = area.X;
            float h = 20f * s;
            using (Font font = Draw.CreateFont(8.5f, FontStyle.Regular, s))
            {
                for (int i = 0; i < options.Count && i < 4; i++)
                {
                    string text = options[i] ?? "";
                    if (!Util.IsVisibleText(text))
                        continue;
                    SizeF measured = g.MeasureString(text, font);
                    float w = measured.Width + 16f * s;
                    if (x + w > area.Right)
                        break;
                    RectangleF chip = new RectangleF(x, area.Y, w, h);
                    using (SolidBrush chipBg = new SolidBrush(Color.FromArgb(30, accent)))
                        Draw.FillRoundRect(g, chipBg, chip, h / 2f);
                    using (Pen chipBorder = new Pen(Color.FromArgb(120, accent), Math.Max(1f, s)))
                        Draw.DrawRoundRect(g, chipBorder, chip, h / 2f);
                    using (SolidBrush chipText = new SolidBrush(Color.FromArgb(235, accent)))
                    using (StringFormat sf = Draw.Format(ContentAlignment.MiddleCenter, false))
                    {
                        g.DrawString(text, font, chipText, chip, sf);
                    }
                    x += w + 6f * s;
                }
            }
        }

        private static void DrawButton(Graphics g, RectangleF rect, string label, bool hover, float s, bool danger)
        {
            using (SolidBrush bg = new SolidBrush(hover ? PetPalette.BtnBgHover : PetPalette.BtnBg))
                Draw.FillRoundRect(g, bg, rect, 6f * s);
            if (hover)
            {
                using (Pen border = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1f, s)))
                    Draw.DrawRoundRect(g, border, rect, 6f * s);
            }
            Color textColor = danger && hover ? Color.FromArgb(255, 255, 170, 170) : PetPalette.TextPrimary;
            using (Font font = Draw.CreateFont(danger ? 11f : 9f, FontStyle.Regular, s))
            using (SolidBrush brush = new SolidBrush(textColor))
            using (StringFormat sf = Draw.Format(ContentAlignment.MiddleCenter, false))
            {
                g.DrawString(label, font, brush, rect, sf);
            }
        }

        private static RectangleF Scaled(RectangleF r, float s)
        {
            return new RectangleF(r.X * s, r.Y * s, r.Width * s, r.Height * s);
        }
    }
}
