// 灵枢桌面版 —— 页面：仪表盘
//
// 数据来自本地会话文件（%APPDATA%\SpiritScout\sessions\*.json），不联网。
// 展示：四个指标卡 + 近 14 天活跃趋势 + 按模式分布 + 最近活动的会话列表。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class DashPage : IPage
    {
        public string Id { get { return "dash"; } }
        public string Title { get { return "仪表盘"; } }
        public PageGroup Group { get { return PageGroup.Main; } }

        private Label[] _metricValue = new Label[4];
        private DataTable _modeGrid;
        private DataTable _recentGrid;
        private Widgets.Sparkline _trend;
        private Label _hint;

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(26, 22, 26, 40),
            };

            var refresh = Theme.Ghost("刷新", 76);
            refresh.Click += async (s, e) => await RefreshAsync().ConfigureAwait(true);
            var head = Theme.PageHeader("仪表盘", "本地数据总览（不上传任何内容）", refresh);

            // 指标卡：四张色晕软块，分类色一眼分开（会话/消息/Token/今日）
            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 108,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 6, 0, 14),
            };
            for (int i = 0; i < 4; i++)
                cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            string[] labels = { "会话总数", "消息总数", "累计 Token", "今日活跃" };
            Color[] hues = { Theme.Accent, Theme.Violet, Theme.Ok, Theme.Warn };
            string[] glyphs = { "◫", "✦", "∑", "◷" };
            for (int i = 0; i < 4; i++)
            {
                var tile = new MetricTile(labels[i], hues[i], glyphs[i])
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 0, 14, 0),
                };
                _metricValue[i] = tile.Value;
                cards.Controls.Add(tile, i, 0);
            }

            // 两张表：各套一个板块卡，表格自己没有边框
            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
            };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));

            _modeGrid = new DataTable { Dock = DockStyle.Fill };
            _modeGrid.AddColumn("mode", "模式", 1.6f);
            _modeGrid.AddColumn("count", "会话", 0.8f, DataGridViewContentAlignment.MiddleRight);
            _modeGrid.AddColumn("tokens", "Token", 1f, DataGridViewContentAlignment.MiddleRight);
            _modeGrid.EmptyText = "还没有会话，所以没有模式分布。";

            _recentGrid = new DataTable { Dock = DockStyle.Fill };
            _recentGrid.AddColumn("target", "最近会话", 2.4f);
            _recentGrid.AddColumn("phase", "状态", 0.9f);
            _recentGrid.MakeChipColumn("phase");   // 状态列画成胶囊
            _recentGrid.AddColumn("msgs", "消息", 0.7f, DataGridViewContentAlignment.MiddleRight);
            _recentGrid.AddColumn("updated", "更新", 1.1f);
            _recentGrid.EmptyText = "还没有活动记录。跑完一个任务，这里会显示最近几次会话的情况。";

            var left = new SectionCard("按模式", "会话与 Token 的分布", Theme.Violet, "◫")
            { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0) };
            left.Body.Controls.Add(_modeGrid);

            var right = new SectionCard("最近活动", "最多 30 条，按更新时间倒序", Theme.Ok, "◷")
            { Dock = DockStyle.Fill };
            right.Body.Controls.Add(_recentGrid);

            split.Controls.Add(left, 0, 0);
            split.Controls.Add(right, 1, 0);

            // 近 14 天活跃趋势：指标卡下面、双表上面
            var trendCard = new SectionCard("近 14 天活跃", "按会话更新日期归桶", Theme.Accent, "◔")
            {
                Dock = DockStyle.Top,
                Height = 196,
                Margin = new Padding(0, 0, 0, 14),
            };
            _trend = new Widgets.Sparkline
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface,
                EmptyText = "还没有会话数据。跑完一个任务，这里会出现近 14 天的活跃曲线。",
            };
            trendCard.Body.Controls.Add(_trend);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            // Dock 的占位顺序是「后加的先生效」：
            //   _hint(Bottom) → head(Top) → cards(Top) → trendCard(Top) → split(Fill)
            root.Controls.Add(split);
            root.Controls.Add(trendCard);
            root.Controls.Add(cards);
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }


        public async Task RefreshAsync()
        {
            if (_metricValue[0] == null || _metricValue[0].IsDisposed) return;

            try
            {
                var store = new Core.SessionStore();
                var sessions = store.List();

                int totalMessages = 0;
                long totalTokens = 0;
                var today = DateTime.Now.Date;
                int todayActive = 0;

                var recent = new List<Dictionary<string, object>>();

                foreach (var s in sessions)
                {
                    // 先用元信息里的条数，避免每个会话都 Load 一遍；
                    // 旧数据（没有这个字段）才退回 Load。
                    int msgs = s.MessageCount;
                    if (msgs == 0)
                    {
                        var sf = await System.Threading.Tasks.Task.Run(
                        () => store.Load(s.Id)).ConfigureAwait(true);
                        msgs = sf == null ? 0 : sf.Messages.Count;
                    }
                    totalMessages += msgs;
                    totalTokens += s.Tokens;
                    if (s.UpdatedAt.Date == today) todayActive++;

                    if (recent.Count < 30)
                    {
                        recent.Add(new Dictionary<string, object>
                        {
                            { "target", s.Target },
                            { "phase", Core.PhaseText.Label(s.Phase) },
                            { "msgs", msgs },
                            { "updated", s.UpdatedAt.ToString("MM-dd HH:mm") },
                        });
                    }
                }

                _metricValue[0].Text = sessions.Count.ToString("N0");
                _metricValue[1].Text = totalMessages.ToString("N0");
                _metricValue[2].Text = Theme.Tokens(totalTokens);
                _metricValue[3].Text = todayActive.ToString("N0");

                // ---- 近 14 天活跃：按 UpdatedAt 的日期归桶 ----
                //
                // 用 UpdatedAt 而不是 CreatedAt：一个跑了三天的长任务，
                // 用户关心的是「这三天它一直在动」，不是「三天前建的」。
                const int TrendDays = 14;
                var dayLabels = new string[TrendDays];
                var day0 = DateTime.Now.Date;
                for (int i = 0; i < TrendDays; i++)
                    dayLabels[i] = day0.AddDays(-(TrendDays - 1 - i)).ToString("MM-dd");

                // 归桶交给 `Session.SessionStats.BucketByDay`（纯函数，可单测）。
                // 那边已经处理了「时间戳在未来」的情况（不判会写到负数下标）。
                var buckets = Session.SessionStats.BucketByDay(sessions, TrendDays, DateTime.Now);
                if (_trend != null && !_trend.IsDisposed) _trend.SetData(buckets, dayLabels);

                // 按模式聚合交给 `Session.SessionStats.ByMode`（纯函数，可单测）。
                // 顺带修了一处：原来把 tokens 截到 int.MaxValue，现在 ModeStat 用 long 不截。
                var modeRows = new List<Dictionary<string, object>>();
                foreach (var st in Session.SessionStats.ByMode(sessions))
                {
                    modeRows.Add(new Dictionary<string, object>
                    {
                        { "mode", st.Mode },
                        { "count", st.Sessions },
                        { "tokens", Theme.Tokens(st.Tokens) },
                    });
                }
                _modeGrid.Fill(modeRows);

                _recentGrid.Fill(recent);
                _recentGrid.Colorize("phase", v =>
                {
                    if (v == "已完成") return Theme.Ok;
                    if (v == "执行中") return Theme.Accent;
                    if (v == "失败" || v == "已停止") return Theme.Danger;
                    if (v == "已暂停") return Theme.Warn;
                    return null;
                });

                _hint.Text = "数据目录：" + Core.AppPaths.DataRoot + "（" + Core.AppPaths.RootKind + "）";
            }
            catch (Exception ex)
            {
                _hint.Text = "统计失败：" + Theme.OneLine(ex.Message);
            }

            await Task.CompletedTask;
        }
    }
}
