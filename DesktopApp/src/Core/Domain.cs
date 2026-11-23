// 灵枢桌面版（纯 C# 重写）—— 领域模型
//
// 这些类型是「程序内部的语言」：会话、任务、消息、模型、经验……
// 它们不含持久化细节（那是 Data 层的事），也不含界面细节（那是 UI 层的事）。
//
// 为什么先定模型：整个重写要分很多轮做。模型稳了，存储、业务、界面才能各自
// 并行推进而不会互相打架。

using System;
using System.Collections.Generic;

namespace SpiritScout.Core
{
    /// <summary>三种工作模式（与 web 端一致）。</summary>
    public enum RunMode
    {
        Src,
        Ctf,
        Competition,
    }

    public static class RunModeText
    {
        /// <summary>
        /// 枚举 → 落盘用的短值。
        ///
        /// **default 故意写成抛异常，不是返回 "ctf"**：
        /// 原来 Ctf 走 default，将来加一个 RunMode.Osint 时，ToValue(Osint) 会
        /// 静默返回 "ctf" —— 存进文件就是错的，而且不报错。
        /// 现在加了新模式忘记加 case，第一次调用就直接炸，改起来一目了然。
        /// （内部枚举值一定合法，抛异常是安全的；容错留给下面的 Parse。）
        /// </summary>
        public static string ToValue(RunMode m)
        {
            switch (m)
            {
                case RunMode.Ctf: return "ctf";
                case RunMode.Src: return "src";
                case RunMode.Competition: return "competition";
                default:
                    throw new ArgumentOutOfRangeException("m",
                        "RunMode 有新成员但 ToValue 没加 case：" + m
                        + "（加模式时要同步改 ToValue / Label / Parse / HomePage 的模式卡）");
            }
        }

        /// <summary>
        /// 这个枚举值是否合法。
        ///
        /// 用途：**从哪里来都要防**。ToValue / Label 对未知值抛异常（为了在开发期
        /// 抓「加了模式忘了加 case」），但反序列化出来的值是**外部数据**——
        /// 用户手工改过、或者文件损坏，都可能给出 (RunMode)999。那种情况不能崩，
        /// 加载时用这个校验一下并修正。
        /// </summary>
        public static bool IsValid(RunMode m)
        {
            return m == RunMode.Ctf || m == RunMode.Src || m == RunMode.Competition;
        }

        public static RunMode Parse(string s)
        {
            // 先 trim：`" src"` / `"ctf "` 这种（比如手改 JSON、或从别处抄来的）
            // 不该被解析成默认值。原来不 trim，`" src"` 会被当成 ctf。
            string t = (s ?? "").Trim();
            if (string.Equals(t, "src", StringComparison.OrdinalIgnoreCase)) return RunMode.Src;
            if (string.Equals(t, "competition", StringComparison.OrdinalIgnoreCase)) return RunMode.Competition;
            return RunMode.Ctf;
        }

        /// <summary>枚举 → 界面显示名。同 ToValue，default 抛异常而非兜底。</summary>
        public static string Label(RunMode m)
        {
            switch (m)
            {
                case RunMode.Ctf: return "CTF 解题";
                case RunMode.Src: return "SRC 渗透";
                case RunMode.Competition: return "智能体竞赛";
                default:
                    throw new ArgumentOutOfRangeException("m",
                        "RunMode 有新成员但 Label 没加 case：" + m);
            }
        }
    }

    /// <summary>会话阶段。</summary>
    public enum SessionPhase
    {
        Pending,
        Executing,
        Paused,
        Completed,
        Failed,
        Stopped,
    }

    public static class PhaseText
    {
        /// <summary>
        /// 枚举 → 落盘短值。default 抛异常，理由同 RunModeText.ToValue：
        /// 原来 Pending 走 default，加新阶段（比如 Timeout）时忘加 case 会
        /// 静默存成 "pending" —— 数据错但不报错。
        /// </summary>
        public static string ToValue(SessionPhase p)
        {
            switch (p)
            {
                case SessionPhase.Pending: return "pending";
                case SessionPhase.Executing: return "executing";
                case SessionPhase.Paused: return "paused";
                case SessionPhase.Completed: return "completed";
                case SessionPhase.Failed: return "failed";
                case SessionPhase.Stopped: return "stopped";
                default:
                    throw new ArgumentOutOfRangeException("p",
                        "SessionPhase 有新成员但 ToValue 没加 case：" + p
                        + "（加阶段时要同步改 ToValue / Label / IsValid）");
            }
        }

        public static SessionPhase Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return SessionPhase.Pending;
            switch (s.Trim().ToLowerInvariant())
            {
                case "executing": case "running": return SessionPhase.Executing;
                case "paused": return SessionPhase.Paused;
                case "completed": case "done": return SessionPhase.Completed;
                case "failed": case "error": return SessionPhase.Failed;
                case "stopped": return SessionPhase.Stopped;
                default: return SessionPhase.Pending;
            }
        }

        /// <summary>枚举 → 界面显示名。default 抛异常，同 ToValue。</summary>
        public static string Label(SessionPhase p)
        {
            switch (p)
            {
                case SessionPhase.Pending: return "待开始";
                case SessionPhase.Executing: return "执行中";
                case SessionPhase.Paused: return "已暂停";
                case SessionPhase.Completed: return "已完成";
                case SessionPhase.Failed: return "失败";
                case SessionPhase.Stopped: return "已停止";
                default:
                    throw new ArgumentOutOfRangeException("p",
                        "SessionPhase 有新成员但 Label 没加 case：" + p);
            }
        }

        /// <summary>这个阶段值是否合法（反序列化出来的外部数据要过这一关）。</summary>
        public static bool IsValid(SessionPhase p)
        {
            return p == SessionPhase.Pending || p == SessionPhase.Executing
                || p == SessionPhase.Paused || p == SessionPhase.Completed
                || p == SessionPhase.Failed || p == SessionPhase.Stopped;
        }
    }

    /// <summary>一个任务会话。</summary>
    public sealed class Session
    {
        /// <summary>数据格式版本（见 Core/DataVersion.cs）。</summary>
        public int SchemaVersion { get; set; }

        public string Id { get; set; }
        public string Target { get; set; }
        public RunMode Mode { get; set; }
        public SessionPhase Phase { get; set; }
        public string Mission { get; set; }
        public string FlagFormat { get; set; }
        public string Title { get; set; }
        public long Tokens { get; set; }

        /// <summary>
        /// 消息条数，写入时同步更新。
        ///
        /// 统计页（用量/仪表盘）只需要「每个会话多少条消息」，
        /// 但原来得把每个会话文件都 Load 一遍才知道——1000 个会话要 290ms。
        /// 存进元信息后，读 List() 就够了。
        ///
        /// 旧数据没有这个字段（=0），页面遇到 0 会退回 Load 一次并回写。
        /// </summary>
        public int MessageCount { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Session()
        {
            Id = "";
            Target = "";
            Mission = "";
            FlagFormat = "";
            Title = "";
            CreatedAt = DateTime.Now;
            UpdatedAt = DateTime.Now;
        }
    }

    /// <summary>会话里的一条消息（用户/助手/工具/系统）。</summary>
    public sealed class Message
    {
        public long Id { get; set; }
        public string SessionId { get; set; }
        public string Role { get; set; }      // user / assistant / tool / system
        public string Content { get; set; }
        public string Name { get; set; }      // 工具名（role=tool 时）
        public DateTime CreatedAt { get; set; }

        public Message()
        {
            SessionId = "";
            Role = "user";
            Content = "";
            Name = "";
            CreatedAt = DateTime.Now;
        }
    }

    /// <summary>一条提炼出来的经验。</summary>
    public sealed class Experience
    {
        public string Id { get; set; }
        public string Insight { get; set; }
        public string Mode { get; set; }        // src / ctf / competition
        public string Tags { get; set; }
        public string SourceSession { get; set; }
        public DateTime CreatedAt { get; set; }

        public Experience()
        {
            Id = "";
            Insight = "";
            Mode = "ctf";
            Tags = "";
            SourceSession = "";
            CreatedAt = DateTime.Now;
        }
    }

    /// <summary>统一的结果封装（业务层返回它，界面据此提示）。</summary>
    public sealed class OpResult<T>
    {
        public bool Ok { get; private set; }
        public T Value { get; private set; }
        public string Error { get; private set; }

        private OpResult() { }

        public static OpResult<T> Success(T value)
        {
            return new OpResult<T> { Ok = true, Value = value };
        }

        public static OpResult<T> Fail(string error)
        {
            return new OpResult<T> { Ok = false, Error = error ?? "未知错误" };
        }
    }

    public static class OpResult
    {
        public static OpResult<bool> Ok()
        {
            return OpResult<bool>.Success(true);
        }

        public static OpResult<bool> Fail(string error)
        {
            return OpResult<bool>.Fail(error);
        }
    }
}
