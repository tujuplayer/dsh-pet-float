// Model.cs
// 配置与状态模型。字段名严格对齐 SPEC.md 第 1 节，不改名。
// 所有字段都有默认值：宿主状态文件缺字段/类型不对时，伴侣要能继续跑。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DshPetFloat
{
    /// <summary>%APPDATA%\dsh-pet\config.json —— 用户偏好，原子写，坏值回落默认。</summary>
    internal sealed class Config
    {
        public int PosX = -1;
        public int PosY = -1;
        public float Scale = 1.0f;
        public bool SoundOn = true;
        public bool EndSoundOn = true;
        public bool ClickOpens = true;
        public int HidePanelMs = 600;
        public bool AutoStart = false;

        public static Config Load(string path)
        {
            Config cfg = new Config();
            try
            {
                if (!File.Exists(path))
                {
                    cfg.Save(path);
                    return cfg;
                }
                JsonValue root;
                if (!JsonParser.TryParse(Util.ReadTextShared(path), out root) || root == null || !root.IsObject)
                {
                    Log.Info("配置读取失败，使用默认值: " + path);
                    return cfg;
                }

                cfg.PosX = root.Int("posX", -1);
                cfg.PosY = root.Int("posY", -1);
                cfg.Scale = (float)root.Num("scale", 1.0);
                cfg.SoundOn = root.Bool("soundOn", true);
                cfg.EndSoundOn = root.Bool("endSoundOn", true);
                cfg.ClickOpens = root.Bool("clickOpens", true);
                cfg.HidePanelMs = root.Int("hidePanelMs", 600);
                cfg.AutoStart = root.Bool("autoStart", false);

                // 脏数据防御：NaN / 极端缩放会让窗口尺寸算出负数
                if (float.IsNaN(cfg.Scale) || float.IsInfinity(cfg.Scale))
                    cfg.Scale = 1.0f;
                if (cfg.Scale < 0.6f) cfg.Scale = 0.6f;
                if (cfg.Scale > 2.0f) cfg.Scale = 2.0f;
                if (cfg.HidePanelMs < 100) cfg.HidePanelMs = 100;
                if (cfg.HidePanelMs > 5000) cfg.HidePanelMs = 5000;
                if (cfg.PosX < -100000 || cfg.PosY < -100000)
                {
                    cfg.PosX = -1;
                    cfg.PosY = -1;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Config.Load", ex);
            }
            return cfg;
        }

        public void Save(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"posX\": ").Append(PosX.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"posY\": ").Append(PosY.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"scale\": ").Append(Scale.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"soundOn\": ").Append(SoundOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"endSoundOn\": ").Append(EndSoundOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"clickOpens\": ").Append(ClickOpens ? "true" : "false").Append(",\r\n");
                sb.Append("  \"hidePanelMs\": ").Append(HidePanelMs.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"autoStart\": ").Append(AutoStart ? "true" : "false").Append("\r\n");
                sb.Append("}\r\n");
                Util.AtomicWrite(path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("Config.Save", ex);
            }
        }
    }

    internal sealed class HostInfo
    {
        public string Version = "";
        public int Pid;
        public int ElectronPid;
        public int Port;
        public string AvatarFile = "";
        public string SoundFile = "";
        public string StateFile = "";
        public string CmdFile = "";
    }

    internal sealed class PageInfo
    {
        public bool Alive;
        public long LastSeenTs;
        public string Title = "";
        public bool Visible;
        public bool Focused;
        public string Href = "";
    }

    internal sealed class QuestionInfo
    {
        public string Text = "";
        public List<string> Options = new List<string>();
    }

    internal sealed class ApprovalInfo
    {
        public string Text = "";
    }

    internal sealed class TodoInfo
    {
        public int Done;
        public int Total;
        public string Current = "";
    }

    internal sealed class TokenInfo
    {
        public long Turn;
        public long Total;
        public long Cached;
    }

    internal sealed class ActiveInfo
    {
        public string Key = "";
        public string Title = "";
        public string Status = "idle"; // idle | running | waiting-question | waiting-approval | done | error
        public string Phase = "idle";   // thinking | tool | waiting | done | idle
        public string Detail = "";
        public string Thinking = "";
        public string ThinkingSource = "";
        public int Turn;
        public int Step;
        public long TurnStartedAt;
        public long ElapsedMs;
        public long WaitingSince;
        public QuestionInfo Question;
        public ApprovalInfo Approval;
        public TodoInfo Todos;
        public TokenInfo Tokens;
        public string LastText = "";
        public long UpdatedAt;

        /// <summary>
        /// elapsedMs 的实时值：running 时按 now - turnStartedAt 算（契约要求），
        /// 否则用宿主给的 elapsedMs。
        /// </summary>
        public long EffectiveElapsedMs()
        {
            if (Status == "running" && TurnStartedAt > 0)
            {
                long delta = Util.NowMs() - TurnStartedAt;
                if (delta >= 0)
                    return delta;
            }
            return ElapsedMs;
        }
    }

    internal sealed class SessionInfo
    {
        public string Key = "";
        public string Title = "";
        public string Status = "idle";
        public long UpdatedAt;
        public bool Current;
    }

    internal sealed class PetEventInfo
    {
        public string Id = "";
        public string Kind = ""; // question | approval | turn-done
        public string Title = "";
        public long Ts;
    }

    internal sealed class PetState
    {
        public int Schema = 1;
        public long Seq;
        public long Ts;
        public HostInfo Host = new HostInfo();
        public PageInfo Page = new PageInfo();
        public ActiveInfo Active; // 可能 null
        public List<SessionInfo> Sessions = new List<SessionInfo>();
        public List<PetEventInfo> Events = new List<PetEventInfo>();
        public int Pending;
        public int Running;

        // 伴侣自己维护的派生状态
        public bool Online;

        public string StatusOrIdle
        {
            get { return Active == null ? "idle" : Active.Status; }
        }

        public static PetState Parse(JsonValue root)
        {
            PetState st = new PetState();
            if (root == null || !root.IsObject)
                return st;

            st.Schema = root.Int("schema", 1);
            st.Seq = root.Long("seq", 0);
            st.Ts = root.Long("ts", 0);

            JsonValue host = root.Obj("host");
            if (host != null)
            {
                st.Host.Version = host.Str("version", "");
                st.Host.Pid = host.Int("pid", 0);
                st.Host.ElectronPid = host.Int("electronPid", 0);
                st.Host.Port = host.Int("port", 0);
                st.Host.AvatarFile = host.Str("avatarFile", "");
                st.Host.SoundFile = host.Str("soundFile", "");
                st.Host.StateFile = host.Str("stateFile", "");
                st.Host.CmdFile = host.Str("cmdFile", "");
            }

            JsonValue page = root.Obj("page");
            if (page != null)
            {
                st.Page.Alive = page.Bool("alive", false);
                st.Page.LastSeenTs = page.Long("lastSeenTs", 0);
                st.Page.Title = page.Str("title", "");
                st.Page.Visible = page.Bool("visible", true);
                st.Page.Focused = page.Bool("focused", false);
                st.Page.Href = page.Str("href", "");
            }

            JsonValue active = root.Obj("active");
            if (active != null)
                st.Active = ParseActive(active);

            JsonValue sessions = root.Arr("sessions");
            if (sessions != null)
            {
                List<JsonValue> items = sessions.SafeItems();
                for (int i = 0; i < items.Count && st.Sessions.Count < 8; i++)
                {
                    JsonValue item = items[i];
                    if (item == null || !item.IsObject)
                        continue;
                    SessionInfo si = new SessionInfo();
                    si.Key = item.Str("key", "");
                    si.Title = item.Str("title", "");
                    si.Status = NormalizeStatus(item.Str("status", "idle"));
                    si.UpdatedAt = item.Long("updatedAt", 0);
                    si.Current = item.Bool("current", false);
                    st.Sessions.Add(si);
                }
            }

            JsonValue events = root.Arr("events");
            if (events != null)
            {
                List<JsonValue> items = events.SafeItems();
                // 环形缓冲最多 20 条，多了只取最后 20
                int start = Math.Max(0, items.Count - 20);
                for (int i = start; i < items.Count; i++)
                {
                    JsonValue item = items[i];
                    if (item == null || !item.IsObject)
                        continue;
                    PetEventInfo ev = new PetEventInfo();
                    ev.Id = item.Str("id", "");
                    ev.Kind = item.Str("kind", "");
                    ev.Title = item.Str("title", "");
                    ev.Ts = item.Long("ts", 0);
                    if (ev.Id.Length == 0)
                        continue; // 没有 id 就无法去重，直接忽略
                    st.Events.Add(ev);
                }
            }

            JsonValue counts = root.Obj("counts");
            if (counts != null)
            {
                st.Pending = counts.Int("pending", 0);
                st.Running = counts.Int("running", 0);
            }

            return st;
        }

        private static ActiveInfo ParseActive(JsonValue active)
        {
            ActiveInfo ai = new ActiveInfo();
            ai.Key = active.Str("key", "");
            ai.Title = active.Str("title", "");
            ai.Status = NormalizeStatus(active.Str("status", "idle"));
            ai.Phase = NormalizePhase(active.Str("phase", "idle"));
            ai.Detail = active.Str("detail", "");
            ai.Thinking = active.Str("thinking", "");
            ai.ThinkingSource = active.Str("thinkingSource", "");
            ai.Turn = active.Int("turn", 0);
            ai.Step = active.Int("step", 0);
            ai.TurnStartedAt = active.Long("turnStartedAt", 0);
            ai.ElapsedMs = active.Long("elapsedMs", 0);
            ai.WaitingSince = active.Long("waitingSince", 0);
            ai.LastText = active.Str("lastText", "");
            ai.UpdatedAt = active.Long("updatedAt", 0);

            JsonValue question = active.Obj("question");
            if (question != null)
            {
                QuestionInfo qi = new QuestionInfo();
                qi.Text = question.Str("text", "");
                JsonValue opts = question.Arr("options");
                if (opts != null)
                {
                    List<JsonValue> items = opts.SafeItems();
                    for (int i = 0; i < items.Count && qi.Options.Count < 8; i++)
                    {
                        JsonValue item = items[i];
                        if (item != null && item.IsString)
                            qi.Options.Add(item.StringValue);
                        else if (item != null)
                            qi.Options.Add(item.Str("label", ""));
                    }
                }
                ai.Question = qi;
            }

            JsonValue approval = active.Obj("approval");
            if (approval != null)
            {
                ApprovalInfo ap = new ApprovalInfo();
                ap.Text = approval.Str("text", "");
                ai.Approval = ap;
            }

            JsonValue todos = active.Obj("todos");
            if (todos != null)
            {
                TodoInfo ti = new TodoInfo();
                ti.Done = todos.Int("done", 0);
                ti.Total = todos.Int("total", 0);
                ti.Current = todos.Str("current", "");
                if (ti.Total > 0)
                    ai.Todos = ti;
            }

            JsonValue tokens = active.Obj("tokens");
            if (tokens != null)
            {
                TokenInfo tk = new TokenInfo();
                tk.Turn = tokens.Long("turn", 0);
                tk.Total = tokens.Long("total", 0);
                tk.Cached = tokens.Long("cached", 0);
                ai.Tokens = tk;
            }

            return ai;
        }

        public static string NormalizeStatus(string raw)
        {
            string s = (raw ?? "").Trim();
            switch (s)
            {
                case "idle":
                case "running":
                case "waiting-question":
                case "waiting-approval":
                case "done":
                case "error":
                    return s;
                default:
                    return "idle";
            }
        }

        public static string NormalizePhase(string raw)
        {
            string s = (raw ?? "").Trim();
            switch (s)
            {
                case "thinking":
                case "tool":
                case "waiting":
                case "done":
                case "idle":
                    return s;
                default:
                    return "idle";
            }
        }
    }
}
