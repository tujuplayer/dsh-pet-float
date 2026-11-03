// Native.cs
// Win32 P/Invoke 集中地 + 分层窗口（逐像素透明）绘制面 + 找窗口/置前工具。
// 为什么全部自绘：契约要求逐像素透明 + 透明像素点击穿透，WinForms 的普通控件做不到。
// 做法：WS_EX_LAYERED + UpdateLayeredWindow(AC_SRC_ALPHA) 提供 BGRA 位图，
// 系统会按像素 alpha 做命中测试，alpha=0 的区域自动把鼠标消息漏给下层窗口。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace DshPetFloat
{
    internal static class Native
    {
        // ---- 窗口样式 ----
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const int HWND_TOP = 0;
        public const int HWND_TOPMOST = -1;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int WM_NCHITTEST = 0x0084;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_DPICHANGED = 0x02E0;

        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;

        // UpdateLayeredWindow 参数
        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        // DPI 感知级别：PER_MONITOR_AWARE_V2 的伪句柄值是 -4
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int CX;
            public int CY;

            public SIZE(int cx, int cy)
            {
                CX = cx;
                CY = cy;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hDc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend,
            int dwFlags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y,
            int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        public const uint SND_ASYNC = 0x0001;
        public const uint SND_NODEFAULT = 0x0002;
        public const uint SND_FILENAME = 0x00020000;

        // ---- 高层封装 ----

        /// <summary>进程级 DPI 感知：优先 PerMonitorV2，失败逐级降级（契约要求）。</summary>
        public static bool EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return true;
            }
            catch (Exception)
            {
            }
            try
            {
                // Win8.1/10 早期：PROCESS_PER_MONITOR_DPI_AWARE = 2
                if (SetProcessDpiAwareness(2) == 0)
                    return true;
            }
            catch (Exception)
            {
            }
            try
            {
                return SetProcessDPIAware();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>返回窗口所在屏幕的 DPI 缩放比（96 DPI = 1.0），取不到时用 fallbackScale。</summary>
        public static float GetWindowDpiScale(IntPtr hwnd, float fallbackScale)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                    return dpi / 96f;
            }
            catch (Exception)
            {
            }
            return fallbackScale;
        }

        public static Point GetCursorPosPoint()
        {
            POINT p;
            if (GetCursorPos(out p))
                return new Point(p.X, p.Y);
            return Point.Empty;
        }

        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>光标下的窗口是不是我们（含子窗口）。用于过滤"球被别的窗口盖住"的假点击。</summary>
        public static bool IsTopWindowAt(Point screenPoint, IntPtr expected)
        {
            POINT p = new POINT(screenPoint.X, screenPoint.Y);
            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero)
                return false;
            if (h == expected)
                return true;
            // 也可能是子窗口/临时窗口，退一步用根窗口比较
            return GetAncestor(h, 2 /*GA_ROOT*/) == expected;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        /// <summary>按 pid 找第一个可见顶层窗口。</summary>
        public static IntPtr FindVisibleWindowByPid(int pid)
        {
            if (pid <= 0)
                return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (!IsWindowVisible(hWnd))
                        return true;
                    if (GetWindowTextLength(hWnd) <= 0)
                        return true;
                    IntPtr procId = IntPtr.Zero;
                    uint windowPid = GetWindowThreadProcessId(hWnd, procId);
                    if ((int)windowPid == pid)
                    {
                        found = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error("FindVisibleWindowByPid", ex);
            }
            return found;
        }

        /// <summary>按标题后缀/包含"DeepSeek Harness"找窗口（契约给的兜底手段）。</summary>
        public static IntPtr FindWindowByTitleSuffix(string suffix)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (!IsWindowVisible(hWnd))
                        return true;
                    int len = GetWindowTextLength(hWnd);
                    if (len <= 0)
                        return true;
                    StringBuilder sb = new StringBuilder(len + 2);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error("FindWindowByTitleSuffix", ex);
            }
            return found;
        }

        public static string GetWindowTitle(IntPtr hwnd)
        {
            try
            {
                int len = GetWindowTextLength(hwnd);
                if (len <= 0)
                    return "";
                StringBuilder sb = new StringBuilder(len + 2);
                GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// 把窗口置前。契约规定顺序：ShowWindow(SW_RESTORE) -> SetForegroundWindow ->
        /// 失败则 AttachThreadInput + SetForegroundWindow -> 再不行 SetWindowPos(HWND_TOP)。
        /// 找不到窗口返回 false，由面板提示"未找到 DSH 窗口"。
        /// </summary>
        public static bool BringWindowToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;
            try
            {
                ShowWindow(hwnd, SW_RESTORE);
                if (SetForegroundWindow(hwnd))
                    return true;

                uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                uint myThread = GetCurrentThreadId();
                bool attached = false;
                try
                {
                    if (foregroundThread != 0 && foregroundThread != myThread)
                        attached = AttachThreadInput(myThread, foregroundThread, true);
                    if (SetForegroundWindow(hwnd))
                        return true;
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(myThread, foregroundThread, false);
                }

                SetWindowPos(hwnd, new IntPtr(HWND_TOP), 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("BringWindowToFront", ex);
                return false;
            }
        }

        public static void SetTopMost(IntPtr hwnd)
        {
            try
            {
                SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception)
            {
            }
        }

        public static void MoveWindow(IntPtr hwnd, int x, int y)
        {
            try
            {
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch (Exception)
            {
            }
        }

        // ── 捏捏音效（SPEC 3.5.3）：PlaySound 是单通道的，连续快捏会把上一声切断；
        // 这里用 MCI 开两个别名通道轮流播，最多同时 2 个实例，也不会打断 PlaySound 播的状态音。
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, System.Text.StringBuilder returnString, int returnLength, IntPtr hwndCallback);

        private static readonly string[] PetSoundAliases = new string[] { "dshpet_a", "dshpet_b" };
        private static int _nextPetSlot;

        /// <summary>用独立的 MCI 通道播捏捏音效；成功返回 true。</summary>
        public static bool PlayWavSlot(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            try
            {
                if (!System.IO.File.Exists(path))
                    return false;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    int slot = (_nextPetSlot + attempt) % PetSoundAliases.Length;
                    string alias = PetSoundAliases[slot];
                    // 先关掉这个通道上一次的声音（它本来也快播完了），再 open+play
                    mciSendString("close " + alias, null, 0, IntPtr.Zero);
                    string open = "open \"" + path + "\" type waveaudio alias " + alias;
                    int rc = mciSendString(open, null, 0, IntPtr.Zero);
                    if (rc != 0)
                        continue;
                    mciSendString("play " + alias + " from 0", null, 0, IntPtr.Zero);
                    _nextPetSlot = (slot + 1) % PetSoundAliases.Length;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("PlayWavSlot", ex);
            }
            return false;
        }

        /// <summary>退出时把两个通道关掉，别让 winmm 里留着句柄。</summary>
        public static void CloseWavSlots()
        {
            try
            {
                for (int i = 0; i < PetSoundAliases.Length; i++)
                    mciSendString("close " + PetSoundAliases[i], null, 0, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error("CloseWavSlots", ex);
            }
        }

        public static void PlayWav(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Log.Info("声音文件不存在，跳过: " + path);
                    return;
                }
                PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
            }
            catch (Exception ex)
            {
                Log.Error("PlayWav", ex);
            }
        }
    }

    /// <summary>
    /// 逐像素透明的绘图面：管理 DIB + UpdateLayeredWindow。
    /// 关键点：GDI+ 画出来的是非预乘 ARGB，而 UpdateLayeredWindow 要求预乘 alpha，
    /// 所以这里手动预乘（否则边缘会有一圈白边/发亮）。
    /// 同时把 alpha 通道缓存下来给 WM_NCHITTEST 做精确命中测试。
    /// </summary>
    internal sealed class LayeredSurface : IDisposable
    {
        private IntPtr _screenDc;
        private IntPtr _memDc;
        private IntPtr _hBitmap;
        private IntPtr _bits;
        private IntPtr _oldBitmap;
        private int _width;
        private int _height;
        private byte[] _alpha;
        private byte[] _row;

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }

        public bool HasPixels { get { return _alpha != null && _width > 0 && _height > 0; } }

        /// <summary>取某个窗口客户区像素的 alpha，越界返回 0（透明）。</summary>
        public byte AlphaAt(int x, int y)
        {
            if (_alpha == null || x < 0 || y < 0 || x >= _width || y >= _height)
                return 0;
            return _alpha[y * _width + x];
        }

        /// <summary>把图像推送到分层窗口；destination 为窗口左上角屏幕坐标。</summary>
        public void Update(IntPtr hwnd, Bitmap bitmap, Point destination, float constantAlpha)
        {
            if (bitmap == null)
                return;
            EnsureSurfaces(bitmap.Width, bitmap.Height);
            FillPremultiplied(bitmap);

            Native.POINT dst = new Native.POINT(destination.X, destination.Y);
            Native.SIZE size = new Native.SIZE(_width, _height);
            Native.POINT src = new Native.POINT(0, 0);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = (byte)Math.Max(0, Math.Min(255, (int)(constantAlpha * 255f)));
            blend.AlphaFormat = Native.AC_SRC_ALPHA;

            Native.UpdateLayeredWindow(hwnd, _screenDc, ref dst, ref size, _memDc, ref src, 0,
                ref blend, Native.ULW_ALPHA);
        }

        private void EnsureSurfaces(int w, int h)
        {
            if (w <= 0 || h <= 0)
                return;
            if (_hBitmap != IntPtr.Zero && _width == w && _height == h)
                return;

            ReleaseSurfaces();

            _screenDc = Native.GetDC(IntPtr.Zero);
            _memDc = Native.CreateCompatibleDC(_screenDc);
            Native.BITMAPINFO bmi = new Native.BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            // 负高度 = 自顶向下 DIB，与 GDI+ 扫描行顺序一致，拷贝时不用翻面
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB
            _bits = IntPtr.Zero;
            _hBitmap = Native.CreateDIBSection(_screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_hBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败");
            _oldBitmap = Native.SelectObject(_memDc, _hBitmap);
            _width = w;
            _height = h;
            _alpha = new byte[w * h];
            _row = new byte[w * 4];
        }

        private void FillPremultiplied(Bitmap bitmap)
        {
            Rectangle rect = new Rectangle(0, 0, _width, _height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                long dstBase = _bits.ToInt64();
                int rowBytes = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    IntPtr srcRow = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    Marshal.Copy(srcRow, _row, 0, rowBytes);
                    int alphaRow = y * _width;
                    for (int x = 0; x < _width; x++)
                    {
                        int i = x * 4;
                        byte a = _row[i + 3];
                        if (a != 0 && a != 255)
                        {
                            // 非预乘 -> 预乘（四舍五入，避免整体偏暗）
                            _row[i] = (byte)((_row[i] * a + 127) / 255);
                            _row[i + 1] = (byte)((_row[i + 1] * a + 127) / 255);
                            _row[i + 2] = (byte)((_row[i + 2] * a + 127) / 255);
                        }
                        else if (a == 0)
                        {
                            _row[i] = 0;
                            _row[i + 1] = 0;
                            _row[i + 2] = 0;
                        }
                        _alpha[alphaRow + x] = a;
                    }
                    Marshal.Copy(_row, 0, new IntPtr(dstBase + (long)y * rowBytes), rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private void ReleaseSurfaces()
        {
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }
            if (_hBitmap != IntPtr.Zero)
            {
                Native.DeleteObject(_hBitmap);
                _hBitmap = IntPtr.Zero;
            }
            if (_memDc != IntPtr.Zero)
            {
                Native.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_screenDc != IntPtr.Zero)
            {
                Native.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }
            _bits = IntPtr.Zero;
            _width = 0;
            _height = 0;
            _alpha = null;
            _row = null;
        }

        public void Dispose()
        {
            ReleaseSurfaces();
        }
    }
}
