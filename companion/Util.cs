// Util.cs
// 路径、原子写、日志、时间格式化等杂项。
// 日志与原子写是稳定性要求的核心：任何异常都不许让进程崩，所以统一走这里。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DshPetFloat
{
    internal static class Paths
    {
        public static string UserProfile
        {
            get
            {
                string p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(p))
                    p = Environment.GetEnvironmentVariable("USERPROFILE");
                if (string.IsNullOrEmpty(p))
                    p = Environment.CurrentDirectory;
                return p;
            }
        }

        public static string DshHome
        {
            get { return Path.Combine(Paths.UserProfile, ".dsh"); }
        }

        public static string DefaultStateFile
        {
            get { return Path.Combine(DshHome, ".dshp-state.json"); }
        }

        public static string DefaultCmdFile
        {
            get { return Path.Combine(DshHome, ".dshp-cmd.json"); }
        }

        public static string AppDataDir
        {
            get
            {
                string p = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(p))
                    p = Path.Combine(Paths.UserProfile, "AppData", "Roaming");
                return Path.Combine(p, "dsh-pet");
            }
        }

        public static string ConfigFile
        {
            get { return Path.Combine(AppDataDir, "config.json"); }
        }

        public static string LogFile
        {
            get { return Path.Combine(AppDataDir, "log.txt"); }
        }

        /// <summary>
        /// "用户主动退出"标记：用户从托盘菜单点退出时由伴侣写入，宿主插件看门狗读到就
        /// 不再自动拉起。伴侣自己启动时会删掉它（启动一次 = 用户又想要它了）。
        /// </summary>
        public static string QuitFlagFile
        {
            get { return Path.Combine(AppDataDir, "quit.flag"); }
        }

        public static string ExePath
        {
            get
            {
                try
                {
                    string p = System.Reflection.Assembly.GetEntryAssembly().Location;
                    if (!string.IsNullOrEmpty(p))
                        return p;
                }
                catch (Exception)
                {
                }
                return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            }
        }
    }

    internal static class Util
    {
        public static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static string TrimToEmpty(string s)
        {
            return s == null ? "" : s;
        }

        /// <summary>
        /// 共享读文本：宿主可能在任意时刻 rename 覆盖，所以必须允许 ReadWrite|Delete 共享，
        /// 并容忍瞬时 IOException（重试几次）。读不到一律返回 null，由调用方走离线分支。
        /// </summary>
        public static string ReadTextShared(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(fs, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(20);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(20);
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>原子写：先写 .tmp 再替换，保证读方永远看到完整 JSON（契约要求）。</summary>
        public static bool AtomicWrite(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string tmp = path + ".tmp";
                byte[] bytes = new UTF8Encoding(false).GetBytes(text == null ? "" : text);
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                    }
                    catch (Exception)
                    {
                        // 某些文件系统不支持 Replace，退化为删除+改名（写方只有我们自己，够安全）
                        try { File.Delete(path); } catch (Exception) { }
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("AtomicWrite " + path, ex);
                return false;
            }
        }

        /// <summary>人类可读耗时：5s / 1m23s / 1h02m。</summary>
        public static string FormatDuration(long ms)
        {
            if (ms < 0)
                ms = 0;
            long totalSeconds = ms / 1000;
            if (totalSeconds < 60)
                return totalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
            long minutes = totalSeconds / 60;
            long seconds = totalSeconds % 60;
            if (minutes < 60)
                return minutes.ToString(CultureInfo.InvariantCulture) + "m" + seconds.ToString("00", CultureInfo.InvariantCulture) + "s";
            long hours = minutes / 60;
            long restMinutes = minutes % 60;
            return hours.ToString(CultureInfo.InvariantCulture) + "h" + restMinutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        public static bool IsVisibleText(string s)
        {
            return !string.IsNullOrEmpty(s) && s.Trim().Length > 0;
        }

        /// <summary>按显示器工作区判断点是否在屏幕内（保存的位置可能来自已拔掉的显示器）。</summary>
        public static bool IsPointOnAnyScreen(int x, int y)
        {
            try
            {
                System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    System.Drawing.Rectangle b = screens[i].Bounds;
                    if (x >= b.Left - 2000 && x <= b.Right + 2000 && y >= b.Top - 2000 && y <= b.Bottom + 2000)
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    /// <summary>
    /// 文件日志。契约要求 &lt;=1MB 截断；这里用简单轮转：超限直接清空重写，
    /// 因为这是排障日志而非审计日志，保留最近内容就够了。
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        private static int _writesSinceCheck;
        private static bool _checkSized;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string dir = Path.GetDirectoryName(Paths.LogFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (!_checkSized)
                    {
                        _checkSized = true;
                        TruncateIfNeeded();
                    }
                    else if (++_writesSinceCheck >= 50)
                    {
                        _writesSinceCheck = 0;
                        TruncateIfNeeded();
                    }

                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                                  + " " + message + "\r\n";
                    File.AppendAllText(Paths.LogFile, line, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // 日志本身失败绝不能影响主流程
            }
        }

        private static void TruncateIfNeeded()
        {
            try
            {
                FileInfo fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    File.Delete(Paths.LogFile);
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Error(string where, Exception ex)
        {
            if (ex == null)
            {
                Write("ERROR " + where);
                return;
            }
            Write("ERROR " + where + " -> " + ex.GetType().Name + ": " + ex.Message
                  + " | " + (ex.StackTrace == null ? "" : ex.StackTrace.Replace("\r\n", " / ").Replace("\n", " / ")));
        }

        public static void Info(string message)
        {
            Write("INFO " + message);
        }
    }
}
