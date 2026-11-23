// 灵枢桌面版 —— 会话统计（纯函数）
//
// ## 为什么抽出来
//
// 「按时间范围过滤」和「按天归桶」这两件事，原来分别写在
// `UsagePage.RefreshAsync` 和 `DashPage.RefreshAsync` 里，各写一套：
//
//   UsagePage：s.UpdatedAt.Date < since  → 跳过
//   DashPage ：(int)(today - s.UpdatedAt.Date).TotalDays → 0..13 才算
//
// **同一件事两种写法**，边界规则（今天算不算、第 7 天算不算、时间回拨怎么办）
// 各写各的，迟早不一致。而且两处都在 UI 闭包里，没法单测。
//
// 抽成这里的纯函数之后：
//   · 边界规则只写一遍
//   · 两个页面调同一套
//   · 可以用固定日期写断言（不用等真实数据）
//
// ## 为什么用 UpdatedAt 而不是 CreatedAt
//
// 三天前建、今天还在跑的长任务，用户关心的是「这三天它一直在动」，
// 不是「三天前建的」。两个页面在这一点上是一致的。

using System;
using System.Collections.Generic;

namespace SpiritScout.Session
{
    /// <summary>
    /// 时间范围档位。数值要和 `Theme.Select` 里那几个选项的顺序对齐
    /// （下拉的 `SelectedIndex` 直接传进来）。
    /// </summary>
    public enum TimeRange
    {
        All = 0,
        Last7Days = 1,
        Last30Days = 2,
        Today = 3,
    }

    /// <summary>按模式聚合的一行。</summary>
    public sealed class ModeStat
    {
        public string Mode { get; set; }
        public int Sessions { get; set; }
        public long Tokens { get; set; }

        public ModeStat()
        {
            Mode = "";
        }
    }

    public static class SessionStats
    {
        /// <summary>
        /// 把下拉框的 `SelectedIndex` 转成档位。越界（含负数）一律当「全部」。
        /// </summary>
        public static TimeRange RangeFromIndex(int index)
        {
            if (index == 1) return TimeRange.Last7Days;
            if (index == 2) return TimeRange.Last30Days;
            if (index == 3) return TimeRange.Today;
            return TimeRange.All;
        }

        /// <summary>档位的中文名（提示栏用）。</summary>
        public static string RangeLabel(TimeRange range)
        {
            switch (range)
            {
                case TimeRange.Last7Days: return "近 7 天";
                case TimeRange.Last30Days: return "近 30 天";
                case TimeRange.Today: return "今天";
                default: return "全部时间";
            }
        }

        /// <summary>
        /// 这个档位的起始日（含当天）。返回 null 表示不设下限。
        ///
        /// `Last7Days` = 今天往前数 6 天（含今天共 7 天）。
        /// `Last30Days` 同理。
        /// </summary>
        public static DateTime? LowerBound(TimeRange range, DateTime now)
        {
            DateTime today = now.Date;
            switch (range)
            {
                case TimeRange.Last7Days: return today.AddDays(-6);
                case TimeRange.Last30Days: return today.AddDays(-29);
                case TimeRange.Today: return today;
                default: return null;
            }
        }

        /// <summary>
        /// 按范围筛选。**用 `UpdatedAt`**（见文件头）。
        ///
        /// 边界：`UpdatedAt.Date == LowerBound` 的那一天**算在范围内**（含当天）。
        /// </summary>
        public static List<Core.Session> FilterByRange(
            IEnumerable<Core.Session> all, TimeRange range, DateTime now)
        {
            var result = new List<Core.Session>();
            if (all == null) return result;

            DateTime? since = LowerBound(range, now);
            long sinceTicks = since.HasValue ? since.Value.Ticks : 0;

            foreach (var s in all)
            {
                if (s == null) continue;
                if (since.HasValue && s.UpdatedAt.Date.Ticks < sinceTicks) continue;
                result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// 按天归桶，返回长度 `days` 的数组，**最后一个元素是「今天」**。
        ///
        /// 只统计 `UpdatedAt` 落在 [今天-(days-1), 今天] 内的会话。
        /// **超出窗口的一律不计**，包括时间戳在未来的那些（时钟回拨/时区问题）——
        /// 不判的话会写到负数下标上。
        /// </summary>
        public static double[] BucketByDay(
            IEnumerable<Core.Session> all, int days, DateTime now)
        {
            if (days <= 0) return new double[0];
            var buckets = new double[days];
            if (all == null) return buckets;

            DateTime today = now.Date;
            foreach (var s in all)
            {
                if (s == null) continue;
                int back = (int)(today - s.UpdatedAt.Date).TotalDays;
                if (back < 0 || back >= days) continue;
                buckets[days - 1 - back] += 1;
            }
            return buckets;
        }

        /// <summary>
        /// 按模式聚合，按会话数降序。
        ///
        /// 模式名用 `Core.RunModeText.Label`，空/未知模式归到「未知」。
        /// </summary>
        public static List<ModeStat> ByMode(IEnumerable<Core.Session> all)
        {
            var map = new Dictionary<string, ModeStat>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            if (all != null)
            {
                foreach (var s in all)
                {
                    if (s == null) continue;
                    string mode;
                    try { mode = Core.RunModeText.Label(s.Mode); }
                    catch { mode = "未知"; }
                    if (string.IsNullOrEmpty(mode)) mode = "未知";

                    ModeStat st;
                    if (!map.TryGetValue(mode, out st))
                    {
                        st = new ModeStat { Mode = mode };
                        map[mode] = st;
                        order.Add(mode);
                    }
                    st.Sessions++;
                    st.Tokens += s.Tokens;
                }
            }

            var list = new List<ModeStat>(order.Count);
            foreach (var k in order) list.Add(map[k]);
            list.Sort(delegate(ModeStat a, ModeStat b) { return b.Sessions.CompareTo(a.Sessions); });
            return list;
        }
    }
}
