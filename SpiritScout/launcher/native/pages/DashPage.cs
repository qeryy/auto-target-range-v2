// 灵枢桌面版 —— 页面：仪表盘
//
// 对应 web 的 view-dash：执行情况、token、费用、缓存命中、近期活动。
// 数据来自 GET /api/dashboard?period=...
//
// 布局：顶部 4 张指标卡 + 下面「按模型 / 每日趋势」两张表。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Native.Pages
{
    public sealed class DashPage : IPage
    {
        public string Id { get { return "dash"; } }
        public string Title { get { return "仪表盘"; } }
        public PageGroup Group { get { return PageGroup.Main; } }

        private readonly AppContext _ctx;
        private ComboBox _period;
        private Label[] _cardValue = new Label[4];
        private Label[] _cardLabel = new Label[4];
        private DataGrid _modelGrid;
        private DataGrid _dailyGrid;
        private Label _hint;

        public DashPage(AppContext ctx) { _ctx = ctx; }

        public Control CreateView()
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = UiKit.Bg, Padding = new Padding(28, 20, 28, 16) };

            _period = UiKit.Select("today", "hour", "7d", "30d", "all");
            _period.Width = 100;
            _period.SelectedIndexChanged += async (s, e) => await RefreshAsync().ConfigureAwait(true);

            var refresh = UiKit.GhostButton("刷新", 76);
            refresh.Click += async (s, e) => await RefreshAsync().ConfigureAwait(true);

            var head = UiKit.PageHeader("仪表盘", "执行、费用与缓存的实时总览", _period, refresh);

            // 指标卡
            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 96,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = UiKit.Bg,
                Padding = new Padding(0, 8, 0, 12),
            };
            for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            string[] labels = { "调用次数", "Token 总量", "估算费用", "缓存命中率" };
            for (int i = 0; i < 4; i++)
            {
                var card = new RoundPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
                _cardLabel[i] = new Label
                {
                    Text = labels[i],
                    ForeColor = UiKit.Muted,
                    Font = UiKit.FontSmall,
                    AutoSize = true,
                    Location = new Point(16, 12),
                };
                _cardValue[i] = new Label
                {
                    Text = "—",
                    ForeColor = UiKit.Text,
                    Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
                    AutoSize = true,
                    Location = new Point(14, 34),
                };
                card.Controls.Add(_cardLabel[i]);
                card.Controls.Add(_cardValue[i]);
                cards.Controls.Add(card, i, 0);
            }

            // 两张表
            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = UiKit.Bg,
            };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _modelGrid = new DataGrid { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
            _modelGrid.AddColumn("model", "模型", 2f);
            _modelGrid.AddColumn("calls", "调用", 0.8f, DataGridViewContentAlignment.MiddleRight);
            _modelGrid.AddColumn("tokens", "Token", 1f, DataGridViewContentAlignment.MiddleRight);
            _modelGrid.AddColumn("share", "占比", 0.8f, DataGridViewContentAlignment.MiddleRight);

            _dailyGrid = new DataGrid { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
            _dailyGrid.AddColumn("day", "日期", 1.2f);
            _dailyGrid.AddColumn("calls", "调用", 0.8f, DataGridViewContentAlignment.MiddleRight);
            _dailyGrid.AddColumn("tokens", "Token", 1f, DataGridViewContentAlignment.MiddleRight);
            _dailyGrid.AddColumn("cost", "费用", 0.9f, DataGridViewContentAlignment.MiddleRight);

            split.Controls.Add(_modelGrid, 0, 0);
            split.Controls.Add(_dailyGrid, 1, 0);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = UiKit.Muted,
                Font = UiKit.FontSmall,
                Text = "",
            };

            root.Controls.Add(split);
            root.Controls.Add(cards);
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }

        public async Task RefreshAsync()
        {
            if (_cardValue[0] == null || _cardValue[0].IsDisposed) return;
            _hint.Text = "正在加载…";
            try
            {
                string period = Convert.ToString(_period.SelectedItem) ?? "today";
                string json = await _ctx.Api.GetAsync("/api/dashboard?period=" + period).ConfigureAwait(true);
                var root = SimpleJson.AsDict(SimpleJson.Parse(json));
                if (root == null) { _hint.Text = "响应格式异常"; return; }

                var summary = GetDict(root, "summary") ?? GetDict(root, "today");
                long calls = GetLong(summary, "calls");
                long tokens = GetLong(summary, "tokens");
                var cost = GetDict(summary, "cost");
                var cache = GetDict(summary, "cache");

                _cardValue[0].Text = calls.ToString("N0");
                _cardValue[1].Text = FormatTokens(tokens);
                _cardValue[2].Text = FormatCost(cost);
                double rate = GetDouble(cache, "hit_rate");
                _cardValue[3].Text = rate > 0 ? (rate * 100).ToString("F1") + "%" : "—";

                // 按模型
                var models = new List<Dictionary<string, object>>();
                object pmRaw;
                if (root.TryGetValue("per_model", out pmRaw))
                {
                    var pm = SimpleJson.AsList(pmRaw) ??
                             (SimpleJson.AsDict(pmRaw) != null
                              ? new List<object>(SimpleJson.AsDict(pmRaw).Values) : null);
                    if (pm != null)
                    {
                        foreach (var it in pm)
                        {
                            var d = SimpleJson.AsDict(it);
                            if (d == null) continue;
                            models.Add(new Dictionary<string, object>
                            {
                                { "model", SimpleJson.Str(d, "model") },
                                { "calls", SimpleJson.Str(d, "calls") },
                                { "tokens", FormatTokens(GetLong(d, "tokens")) },
                                { "share", GetDouble(d, "share") > 0
                                    ? (GetDouble(d, "share") * 100).ToString("F1") + "%" : "—" },
                            });
                        }
                    }
                }
                _modelGrid.Fill(models);

                // 每日
                var days = new List<Dictionary<string, object>>();
                object dailyRaw;
                if (root.TryGetValue("daily", out dailyRaw))
                {
                    var dl = SimpleJson.AsList(dailyRaw) ??
                             (SimpleJson.AsDict(dailyRaw) != null
                              ? new List<object>(SimpleJson.AsDict(dailyRaw).Values) : null);
                    if (dl != null)
                    {
                        foreach (var it in dl)
                        {
                            var d = SimpleJson.AsDict(it);
                            if (d == null) continue;
                            days.Add(new Dictionary<string, object>
                            {
                                { "day", SimpleJson.Str(d, "day") },
                                { "calls", SimpleJson.Str(d, "calls") },
                                { "tokens", FormatTokens(GetLong(d, "tokens")) },
                                { "cost", FormatCost(GetDict(d, "cost")) },
                            });
                        }
                    }
                }
                _dailyGrid.Fill(days);

                _hint.Text = string.Format("时段：{0} · 会话 {1} 个 · 运行中 {2} 个 · 生成于 {3}",
                    SimpleJson.Str(root, "period"), SimpleJson.Str(root, "total_sessions"),
                    SimpleJson.Str(root, "running"), SimpleJson.Str(root, "generated_at"));
            }
            catch (Exception ex)
            {
                _hint.Text = "加载失败：" + UiKit.OneLine(ex.Message);
            }
        }

        // ---------- 小工具 ----------

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? SimpleJson.AsDict(v) : null;
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            long n;
            if (long.TryParse(v.ToString(), out n)) return n;
            double f;
            if (double.TryParse(v.ToString(), out f)) return (long)f;
            return 0;
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            double f;
            return double.TryParse(v.ToString(), out f) ? f : 0;
        }

        internal static string FormatTokens(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("F2") + "M";
            if (n >= 1000) return (n / 1000.0).ToString("F1") + "K";
            return n.ToString();
        }

        private static string FormatCost(Dictionary<string, object> cost)
        {
            if (cost == null) return "—";
            double usd = GetDouble(cost, "usd");
            double cnyMax = GetDouble(cost, "cny_max");
            if (usd > 0) return "$" + usd.ToString("F4");
            if (cnyMax > 0) return "¥" + cnyMax.ToString("F3");
            long priced = GetLong(cost, "priced_calls");
            return priced == 0 ? "未计价" : "¥0";
        }
    }
}
