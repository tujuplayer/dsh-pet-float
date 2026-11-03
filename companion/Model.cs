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

        // ---- v1.1 增补（SPEC 3.5.5）----
        public bool BubbleOn = true;
        public int BubbleIdleSec = 240;
        public bool PetSoundOn = true;
        public int PetCount = 0;
        // 空数组 = 用内置默认台词（见 WalletFormat.DefaultBubbles / DefaultPetLines）
        public List<string> Bubbles = new List<string>();
        public List<string> PetLines = new List<string>();
        // v1.1.1：状态边沿气泡按状态分池（留空 = 内置默认，见 WalletFormat.DefaultQuestionLines 等）
        public List<string> QuestionLines = new List<string>();
        public List<string> ApprovalLines = new List<string>();
        public List<string> DoneLines = new List<string>();
        public List<string> ErrorLines = new List<string>();

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

                cfg.BubbleOn = root.Bool("bubbleOn", true);
                cfg.BubbleIdleSec = root.Int("bubbleIdleSec", 240);
                cfg.PetSoundOn = root.Bool("petSoundOn", true);
                cfg.PetCount = root.Int("petCount", 0);
                cfg.Bubbles = ReadLines(root, "bubbles");
                cfg.PetLines = ReadLines(root, "petLines");

                // 状态气泡分池：{ "statusLines": { "question": [...], "approval": [...], ... } }
                JsonValue statusLines = root.Obj("statusLines");
                if (statusLines != null)
                {
                    cfg.QuestionLines = ReadLinesFrom(statusLines, "question");
                    cfg.ApprovalLines = ReadLinesFrom(statusLines, "approval");
                    cfg.DoneLines = ReadLinesFrom(statusLines, "done");
                    cfg.ErrorLines = ReadLinesFrom(statusLines, "error");
                }

                // 脏数据防御：NaN / 极端缩放会让窗口尺寸算出负数
                if (float.IsNaN(cfg.Scale) || float.IsInfinity(cfg.Scale))
                    cfg.Scale = 1.0f;
                if (cfg.Scale < 0.6f) cfg.Scale = 0.6f;
                if (cfg.Scale > 2.0f) cfg.Scale = 2.0f;
                if (cfg.HidePanelMs < 100) cfg.HidePanelMs = 100;
                if (cfg.HidePanelMs > 5000) cfg.HidePanelMs = 5000;
                if (cfg.BubbleIdleSec < 30) cfg.BubbleIdleSec = 30;
                if (cfg.BubbleIdleSec > 7200) cfg.BubbleIdleSec = 7200;
                if (cfg.PetCount < 0) cfg.PetCount = 0;
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

        /// <summary>读台词数组：只收非空字符串、最多 20 条、每条截到 120 字，脏数据不炸。</summary>
        private static List<string> ReadLines(JsonValue root, string name)
        {
            return ReadLinesFrom(root, name);
        }

        private static List<string> ReadLinesFrom(JsonValue root, string name)
        {
            List<string> list = new List<string>();
            if (root == null)
                return list;
            JsonValue arr = root.Arr(name);
            if (arr == null)
                return list;
            List<JsonValue> items = arr.SafeItems();
            for (int i = 0; i < items.Count && list.Count < 20; i++)
            {
                JsonValue item = items[i];
                if (item == null || !item.IsString)
                    continue;
                string text = Util.TrimToEmpty(item.StringValue);
                if (text.Length == 0)
                    continue;
                if (text.Length > 120)
                    text = text.Substring(0, 120);
                list.Add(text);
            }
            return list;
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
                sb.Append("  \"autoStart\": ").Append(AutoStart ? "true" : "false").Append(",\r\n");
                sb.Append("  \"bubbleOn\": ").Append(BubbleOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"bubbleIdleSec\": ").Append(BubbleIdleSec.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"petSoundOn\": ").Append(PetSoundOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"petCount\": ").Append(PetCount.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"bubbles\": ").Append(SerializeLines(Bubbles)).Append(",\r\n");
                sb.Append("  \"petLines\": ").Append(SerializeLines(PetLines)).Append(",\r\n");
                sb.Append("  \"statusLines\": {\r\n");
                sb.Append("    \"question\": ").Append(SerializeLines(QuestionLines)).Append(",\r\n");
                sb.Append("    \"approval\": ").Append(SerializeLines(ApprovalLines)).Append(",\r\n");
                sb.Append("    \"done\": ").Append(SerializeLines(DoneLines)).Append(",\r\n");
                sb.Append("    \"error\": ").Append(SerializeLines(ErrorLines)).Append("\r\n");
                sb.Append("  }\r\n");
                sb.Append("}\r\n");
                Util.AtomicWrite(path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("Config.Save", ex);
            }
        }

        private static string SerializeLines(List<string> lines)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            if (lines != null)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(JsonText.Quote(lines[i] ?? ""));
                }
            }
            sb.Append("]");
            return sb.ToString();
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
        // v1.1：插件包自带的捏捏音效，由宿主自举复制并告知路径（优先级最高）
        public string PetSoundFile = "";
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

    /// <summary>v1.1：宿主实时钱包快照（SPEC 3.5.1）。缺字段一律当“未知”，不报错。</summary>
    internal sealed class WalletInfo
    {
        public bool Ok;
        public string Source = "none";
        public string Currency = "CNY";
        public double? Balance;
        public double? DayStart;
        public double? TodaySpend;
        public long TodayTokens;
        public bool Partial;
        public long UpdatedAt;
        public string Error = "";
    }

    /// <summary>
    /// 钱包与台词的格式化工具。集中放这里，是因为面板钱包行、气泡占位符、
    /// 右键菜单三处都要用同一套数字写法（¥77.72 / 15.2万 tokens），分开写迟早不一致。
    /// </summary>
    internal static class WalletFormat
    {
        public const string DefaultBubble1 = "小肥鱼在摸鱼，但 DSH 在跑。";
        public const string DefaultBubble2 = "余额 ¥{balance}，今天已经花掉 ¥{today} 了。";
        public const string DefaultBubble3 = "{title} 还在跑呢。";
        public const string DefaultBubble4 = "今日 {tokens} tokens，钱包在哭。";
        public const string DefaultBubble5 = "要我帮忙盯着吗？";

        public const string DefaultPet1 = "别捏啦，会扁的！";
        public const string DefaultPet2 = "唔…再来一下。";
        public const string DefaultPet3 = "捏我干嘛，去写代码。";
        public const string DefaultPet4 = "手感不错吧？";
        public const string DefaultPet5 = "再捏我可要收费了（¥{balance} 只够捏这么多）。";

        public static readonly string[] DefaultBubbles = new string[]
        {
            DefaultBubble1, DefaultBubble2, DefaultBubble3, DefaultBubble4, DefaultBubble5
        };

        public static readonly string[] DefaultPetLines = new string[]
        {
            DefaultPet1, DefaultPet2, DefaultPet3, DefaultPet4, DefaultPet5
        };

        // v1.1.1：状态边沿气泡分池（Lead m00728 拍板的内置默认文案）
        public static readonly string[] DefaultQuestionLines = new string[]
        {
            "{title} 在等你回答。", "轮到你了。"
        };

        public static readonly string[] DefaultApprovalLines = new string[]
        {
            "要你点头才能继续。"
        };

        public static readonly string[] DefaultDoneLines = new string[]
        {
            "这轮跑完了，伸个懒腰。", "收工，喝口水。"
        };

        public static readonly string[] DefaultErrorLines = new string[]
        {
            "出错了，去看一眼？"
        };

        /// <summary>状态 → 台词池的 key；无关状态返回 null。</summary>
        public static string StatusKey(string status)
        {
            switch (status)
            {
                case "waiting-question": return "question";
                case "waiting-approval": return "approval";
                case "done": return "done";
                case "error": return "error";
                default: return null;
            }
        }

        /// <summary>取该状态的内置默认池；未知 key 返回空数组。</summary>
        public static string[] DefaultLinesFor(string key)
        {
            switch (key)
            {
                case "question": return DefaultQuestionLines;
                case "approval": return DefaultApprovalLines;
                case "done": return DefaultDoneLines;
                case "error": return DefaultErrorLines;
                default: return new string[0];
            }
        }

        /// <summary>货币符号：USD 用 $，其它一律 ¥（宿主目前只有 CNY/USD）。</summary>
        public static string Symbol(string currency)
        {
            return string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "¥";
        }

        /// <summary>金额两位小数；null 给空串（调用方据此判断“取不到值”）。</summary>
        public static string Money(double? value)
        {
            if (!value.HasValue)
                return "";
            double v = value.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
                return "";
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>token 短写：12345 → 1.2万；152000 → 15.2万；1.5亿以上用亿。</summary>
        public static string TokensShort(long tokens)
        {
            if (tokens <= 0)
                return "0";
            if (tokens >= 100000000L)
            {
                double yi = tokens / 100000000.0;
                return yi.ToString(yi >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + "亿";
            }
            if (tokens >= 10000L)
            {
                double wan = tokens / 10000.0;
                return wan.ToString(wan >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + "万";
            }
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 面板钱包行（SPEC 3.5.4）：离线或没有 wallet 字段时给空串（面板保留行高，不显示假数据）。
        /// </summary>
        public static string PanelLine(PetState st, bool online)
        {
            if (!online || st == null || st.Wallet == null)
                return "";
            WalletInfo w = st.Wallet;
            if (!w.Ok)
                return "余额未知";
            string sym = Symbol(w.Currency);
            string balance = w.Balance.HasValue ? "余额 " + sym + Money(w.Balance) : "余额未知";
            string today;
            // v1.1.1：updatedAt 超过 10 分钟没刷新，说明今日花费可能是滞后的估算，同样加 ≥
            bool stale = w.UpdatedAt > 0 && Util.NowMs() - w.UpdatedAt > 10L * 60L * 1000L;
            if (!w.TodaySpend.HasValue)
                today = "今日 --";
            else
                today = "今日 " + (w.Partial || stale ? "≥" : "") + sym + Money(w.TodaySpend);
            return balance + " · " + today + " · " + TokensShort(w.TodayTokens) + " tokens";
        }

        /// <summary>取占位符值；返回 null 表示“这条台词用不了”，调用方跳下一条。</summary>
        public static string ResolvePlaceholder(string name, PetState st, bool online)
        {
            WalletInfo w = online && st != null ? st.Wallet : null;
            switch (name)
            {
                case "balance":
                    if (w == null || !w.Ok || !w.Balance.HasValue)
                        return null;
                    return Money(w.Balance);
                case "today":
                    if (w == null || !w.Ok || !w.TodaySpend.HasValue)
                        return null;
                    return Money(w.TodaySpend);
                case "tokens":
                    if (w == null || !w.Ok || w.TodayTokens <= 0)
                        return null;
                    return TokensShort(w.TodayTokens);
                case "title":
                    if (st == null || st.Active == null || !Util.IsVisibleText(st.Active.Title))
                        return null;
                    return st.Active.Title.Trim();
                default:
                    return null;
            }
        }

        /// <summary>
        /// 把一条台词里的 {balance}/{today}/{title}/{tokens} 全部替换；任一占位符取不到值就返回 null。
        /// 未知占位符保留原样（不让用户的自由文本炸掉）。
        /// </summary>
        public static string Render(string raw, PetState st, bool online)
        {
            if (!Util.IsVisibleText(raw))
                return null;
            string text = raw.Trim();
            int guard = 0;
            while (guard++ < 12)
            {
                int start = text.IndexOf('{');
                if (start < 0)
                    return text;
                int end = text.IndexOf('}', start + 1);
                if (end < 0)
                    return text;
                string name = text.Substring(start + 1, end - start - 1).Trim();
                bool known = name == "balance" || name == "today" || name == "title" || name == "tokens";
                if (!known)
                {
                    // 未知占位符：把左花括号换成全角，避免死循环，内容尽量原样留着
                    text = text.Substring(0, start) + "｛" + text.Substring(start + 1);
                    continue;
                }
                string value = ResolvePlaceholder(name, st, online);
                if (value == null)
                    return null;
                text = text.Substring(0, start) + value + text.Substring(end + 1);
            }
            return text;
        }

        /// <summary>从候选台词里随机挑第一条“能用”的；全都用不了返回 null。</summary>
        public static string Pick(string[] lines, PetState st, bool online, Random rng)
        {
            if (lines == null || lines.Length == 0)
                return null;
            // 从头随机起点扫一圈，保证公平且只扫一遍
            int n = lines.Length;
            int offset = rng == null ? 0 : rng.Next(n);
            for (int i = 0; i < n; i++)
            {
                string line = lines[(offset + i) % n];
                string text = Render(line, st, online);
                if (text != null)
                    return text;
            }
            return null;
        }

        /// <summary>配置里的空数组回落到内置默认台词。</summary>
        public static string[] Effective(List<string> configured, string[] defaults)
        {
            if (configured != null && configured.Count > 0)
                return configured.ToArray();
            return defaults;
        }
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
        public WalletInfo Wallet; // v1.1：可能 null
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
                st.Host.PetSoundFile = host.Str("petSoundFile", "");
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

            JsonValue wallet = root.Obj("wallet");
            if (wallet != null)
                st.Wallet = ParseWallet(wallet);

            JsonValue counts = root.Obj("counts");
            if (counts != null)
            {
                st.Pending = counts.Int("pending", 0);
                st.Running = counts.Int("running", 0);
            }

            return st;
        }

        private static WalletInfo ParseWallet(JsonValue wallet)
        {
            WalletInfo w = new WalletInfo();
            w.Ok = wallet.Bool("ok", false);
            w.Source = wallet.Str("source", "none");
            string currency = wallet.Str("currency", "CNY");
            w.Currency = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "CNY";
            w.Balance = ReadNullableNumber(wallet.Get("balance"));
            w.DayStart = ReadNullableNumber(wallet.Get("dayStart"));
            w.TodaySpend = ReadNullableNumber(wallet.Get("todaySpend"));
            w.TodayTokens = wallet.Long("todayTokens", 0);
            if (w.TodayTokens < 0)
                w.TodayTokens = 0;
            w.Partial = wallet.Bool("partial", false);
            w.UpdatedAt = wallet.Long("updatedAt", 0);
            w.Error = wallet.Str("error", "");
            return w;
        }

        /// <summary>数字或 null 都要能吃下；字符串形式的数字也顺手兼容。</summary>
        private static double? ReadNullableNumber(JsonValue value)
        {
            if (value == null || value.IsNull)
                return null;
            if (value.Kind == JsonKind.Number)
            {
                double d = value.NumberValue;
                if (double.IsNaN(d) || double.IsInfinity(d) || d < 0)
                    return null;
                return d;
            }
            if (value.Kind == JsonKind.String)
            {
                double parsed;
                if (double.TryParse(value.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0)
                        return null;
                    return parsed;
                }
            }
            return null;
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
