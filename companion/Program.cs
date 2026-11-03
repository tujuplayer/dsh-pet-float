// Program.cs
// 进程入口：参数解析、单实例 Mutex、DPI 感知、隐藏自测开关。
//
// 隐藏自测（不影响正常运行）：
//   --selftest-open          启动 1.2 秒后写一条 open 命令并退出，用来核对命令文件格式
//   --selftest-render <dir>  把所有状态渲染成 PNG 后退出，用来核对视觉/排版
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshPetFloat
{
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static int Main(string[] args)
        {
            PetOptions opt = ParseArgs(args);

            // 契约：PerMonitorV2，失败再退到 System DPI Aware。必须在建窗口之前调。
            try
            {
                if (!Native.EnableDpiAwareness())
                    Log.Info("DPI 感知设置回退到 SystemDpiAware");
            }
            catch (Exception ex)
            {
                Log.Error("设置 DPI 感知", ex);
            }

            // 渲染自测不需要常驻，也不占用单实例名额
            if (!string.IsNullOrEmpty(opt.SelftestRenderDir))
                return RunSelftestRender(opt.SelftestRenderDir);

            // 契约：单实例，已有实例时静默退出（退出码 0）
            bool createdNew;
            _mutex = new Mutex(true, "Local\\DshPetFloat", out createdNew);
            if (!createdNew)
            {
                Log.Info("已有 DshPetFloat 实例在运行，本次直接退出");
                return 0;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                PetApp app = new PetApp(opt);
                app.Start();
                Application.Run(app);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("主循环异常退出", ex);
                return 1;
            }
            finally
            {
                try
                {
                    if (_mutex != null)
                    {
                        _mutex.ReleaseMutex();
                        _mutex.Dispose();
                        _mutex = null;
                    }
                }
                catch (Exception)
                {
                    // 退出路径上的异常不再记日志，免得日志写一半又抛
                }
            }
        }

        private static PetOptions ParseArgs(string[] args)
        {
            PetOptions opt = new PetOptions();
            string state = null;
            string cmd = null;
            string demo = null;
            string render = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                switch (a.ToLowerInvariant())
                {
                    case "--state":
                        if (i + 1 < args.Length)
                            state = args[++i];
                        break;
                    case "--cmd":
                        if (i + 1 < args.Length)
                            cmd = args[++i];
                        break;
                    case "--demo":
                        if (i + 1 < args.Length)
                            demo = args[++i];
                        break;
                    case "--selftest-open":
                        opt.SelftestOpen = true;
                        break;
                    case "--selftest-quit":
                        opt.SelftestQuit = true;
                        break;
                    case "--selftest-render":
                        if (i + 1 < args.Length)
                            render = args[++i];
                        break;
                    default:
                        if (a.Length > 0)
                            Log.Info("忽略未知参数: " + a);
                        break;
                }
            }

            if (!string.IsNullOrEmpty(demo))
            {
                // 演示模式：只读演示状态；命令文件默认不写，配置也不落盘，
                // 这样演示不会污染真实用户配置和宿主的命令通道。
                opt.Demo = true;
                opt.StatePath = ResolvePath(demo);
                opt.CmdWritable = false;
                opt.SaveConfig = false;
                if (!string.IsNullOrEmpty(cmd))
                    opt.CmdPath = ResolvePath(cmd);
            }
            else
            {
                opt.StatePath = string.IsNullOrEmpty(state) ? Paths.DefaultStateFile : ResolvePath(state);
                opt.CmdPath = string.IsNullOrEmpty(cmd) ? Paths.DefaultCmdFile : ResolvePath(cmd);
            }

            opt.SelftestRenderDir = string.IsNullOrEmpty(render) ? null : ResolvePath(render);
            return opt;
        }

        /// <summary>相对路径先按当前目录解析，再退回 exe 所在目录（免得用户必须 cd）。</summary>
        private static string ResolvePath(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0)
                return path;
            try
            {
                if (Path.IsPathRooted(path))
                    return path;
                string cwd = Path.GetFullPath(path);
                if (File.Exists(cwd))
                    return cwd;
                string exeDir = Path.GetDirectoryName(Paths.ExePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string near = Path.Combine(exeDir, path);
                    if (File.Exists(near))
                        return near;
                }
                return cwd;
            }
            catch (Exception ex)
            {
                Log.Error("解析路径 " + path, ex);
                return path;
            }
        }

        // ───────────────────── 渲染自测 ─────────────────────

        private static int RunSelftestRender(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                List<string> manifest = new List<string>();
                Log.Info("渲染自测输出到 " + dir);

                SaveBall(dir, "ball-idle.png", "idle", true, false, 0, manifest);
                SaveBall(dir, "ball-running.png", "running", true, false, 900, manifest);
                SaveBall(dir, "ball-hover.png", "running", true, true, 900, manifest);
                SaveBall(dir, "ball-question.png", "waiting-question", true, false, 780, manifest);
                SaveBall(dir, "ball-approval.png", "waiting-approval", true, false, 640, manifest);
                SaveBall(dir, "ball-done.png", "done", true, false, 0, manifest);
                SaveBall(dir, "ball-error.png", "error", true, false, 0, manifest);
                SaveBall(dir, "ball-offline.png", "idle", false, false, 0, manifest);

                PetState running = DemoState("running");
                PetState question = DemoState("waiting-question");
                PetState approval = DemoState("waiting-approval");
                PetState done = DemoState("done");

                SavePanel(dir, "panel-running.png", running, true, false, 1.0f, "title", "", manifest);
                SavePanel(dir, "panel-question.png", question, true, false, 1.0f, "", "", manifest);
                SavePanel(dir, "panel-approval.png", approval, true, false, 1.0f, "open", "", manifest);
                SavePanel(dir, "panel-done.png", done, true, false, 1.0f, "", "", manifest);
                SavePanel(dir, "panel-offline.png", running, false, false, 1.0f, "", "", manifest);
                SavePanel(dir, "panel-sessions.png", running, true, true, 1.0f, "session:1", "", manifest);
                SavePanel(dir, "panel-notice.png", running, true, false, 1.0f, "", "未找到 DSH 窗口", manifest);
                SavePanel(dir, "panel-running-15x.png", running, true, false, 1.5f, "", "", manifest);

                File.WriteAllText(Path.Combine(dir, "render-manifest.txt"),
                    "渲染自测产物 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n\r\n"
                    + string.Join("\r\n", manifest.ToArray()) + "\r\n", Encoding.UTF8);
                Log.Info("渲染自测完成，共 " + manifest.Count + " 张");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("渲染自测", ex);
                return 1;
            }
        }

        private static void SaveBall(string dir, string name, string status, bool online, bool hover,
            double animMs, List<string> manifest)
        {
            BallArtArgs args = new BallArtArgs();
            args.Status = status;
            args.Phase = status == "running" ? "tool" : "waiting";
            args.Online = online;
            args.Hover = hover;
            args.AnimMs = animMs;
            args.Scale = 1.0f;
            using (Bitmap bmp = PetArt.RenderBall(args))
            {
                string path = Path.Combine(dir, name);
                bmp.Save(path, ImageFormat.Png);
                manifest.Add(name + "  " + bmp.Width + "x" + bmp.Height + "  status=" + status
                             + " online=" + online + " hover=" + hover + " anim=" + animMs);
            }
        }

        private static void SavePanel(string dir, string name, PetState state, bool online,
            bool sessions, float scale, string hoverKey, string notice, List<string> manifest)
        {
            PetArt.PanelArtArgs args = new PetArt.PanelArtArgs();
            args.State = state;
            args.Online = online;
            args.SessionListOpen = sessions;
            args.SoundOn = true;
            args.HoverKey = hoverKey ?? "";
            args.Notice = notice ?? "";
            args.AnimMs = 950;
            args.Scale = scale;

            PetArt.PanelLayout layout;
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                layout = PetArt.ComputePanelLayout(args, g);
            }
            using (Bitmap bmp = PetArt.RenderPanel(args, layout))
            {
                string path = Path.Combine(dir, name);
                bmp.Save(path, ImageFormat.Png);
                manifest.Add(name + "  " + bmp.Width + "x" + bmp.Height
                             + "  status=" + (state.Active != null ? state.Active.Status : "null")
                             + " online=" + online + " sessions=" + sessions + " hover=" + hoverKey
                             + "  h=" + layout.HeightDip);
            }
        }

        /// <summary>自测用的假状态：覆盖问题/授权/待办/会话列表等所有分支。</summary>
        private static PetState DemoState(string status)
        {
            PetState st = new PetState();
            st.Schema = 1;
            st.Seq = 42;
            st.Ts = Util.NowMs();
            st.Host.Version = "0.1.0";
            st.Page.Alive = true;
            st.Page.Title = "和小肥鱼聊天";
            st.Page.Visible = true;
            st.Page.Focused = true;

            ActiveInfo a = new ActiveInfo();
            a.Key = "demo-key";
            a.Title = "实现桌面宠物悬浮球的状态面板与交互";
            a.Status = status;
            a.Phase = status == "running" ? "thinking" : "waiting";
            a.Detail = "正在按 SPEC 第 3 节整理悬浮球窗口的绘制顺序";
            a.Thinking = "先把 UpdateLayeredWindow 封装好，再做命中测试与拖动…";
            a.Turn = 3;
            a.Step = 7;
            a.TurnStartedAt = Util.NowMs() - 83000;
            a.ElapsedMs = 83000;
            a.Todos = new TodoInfo();
            a.Todos.Done = 2;
            a.Todos.Total = 5;
            a.Todos.Current = "面板排版";
            a.Tokens = new TokenInfo();
            a.Tokens.Turn = 12840;
            a.Tokens.Total = 560000;
            a.Tokens.Cached = 410000;

            if (status == "waiting-question")
            {
                a.Question = new QuestionInfo();
                a.Question.Text = "悬浮球默认贴在屏幕右下角，还是左上角？";
                a.Question.Options.Add("右下角（推荐）");
                a.Question.Options.Add("左上角");
            }
            if (status == "waiting-approval")
            {
                a.Approval = new ApprovalInfo();
                a.Approval.Text = "要执行 taskkill /F /IM DshPetFloat.exe 清理测试实例，允许吗？";
            }
            st.Active = a;

            AddSession(st, "sess-1", "实现桌面宠物悬浮球的状态面板与交互", "running", true);
            AddSession(st, "sess-2", "修复页面桥 /dsh-pet/cmd-ack 的重复确认", "done", false);
            AddSession(st, "sess-3", "整理宿主插件路由表", "idle", false);
            AddSession(st, "sess-4", "写 README 与自测脚本", "idle", false);
            return st;
        }

        private static void AddSession(PetState st, string key, string title, string status, bool current)
        {
            SessionInfo s = new SessionInfo();
            s.Key = key;
            s.Title = title;
            s.Status = status;
            s.UpdatedAt = Util.NowMs();
            s.Current = current;
            st.Sessions.Add(s);
        }
    }
}
