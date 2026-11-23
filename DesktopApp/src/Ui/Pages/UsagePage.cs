// 灵枢桌面版 —— 页面：用量
//
// 按会话和按天两个维度统计 token 消耗。数据来自本地会话文件。
// 桌面版没有 web 端那种「按供应商计价」的账本——单机使用，token 数就是最直接的口径。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class UsagePage : IPage
    {
        public string Id { get { return "usage"; } }
        public string Title { get { return "用量"; } }
        public PageGroup Group { get { return PageGroup.Main; } }

        private Label[] _metric = new Label[4];
        private DataTable _grid;
        private Label _hint;
        /// <summary>时间范围筛选（全部 / 近 7 天 / 近 30 天 / 今天）。</summary>
        private ComboBox _range;

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

            // 时间范围：按会话的 UpdatedAt 过滤。
            // 用预设而不是两个日期选择器 —— 用量页的问题是「最近花了多少」，
            // 预设档位够用，而且不会出现「起始 > 结束」这种要校验的情况。
            _range = Theme.Select("全部时间", "近 7 天", "近 30 天", "今天");
            _range.Width = 110;
            _range.SelectedIndexChanged += async (s, e) => await RefreshAsync().ConfigureAwait(true);

            var head = Theme.PageHeader("用量", "按会话统计 token 消耗", _range, refresh);

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

            string[] labels = { "累计 Token", "平均每会话", "最高单会话", "估算成本" };
            Color[] hues = { Theme.Accent, Theme.Violet, Theme.Ok, Theme.Warn };
            string[] glyphs = { "∑", "◫", "▲", "¥" };
            for (int i = 0; i < 4; i++)
            {
                var tile = new MetricTile(labels[i], hues[i], glyphs[i])
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 0, 14, 0),
                };
                _metric[i] = tile.Value;
                cards.Controls.Add(tile, i, 0);
            }

            _grid = new DataTable { Dock = DockStyle.Fill };
            _grid.AddColumn("target", "会话", 2.6f);
            _grid.AddColumn("mode", "模式", 0.9f);
            _grid.AddColumn("msgs", "消息", 0.7f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("tokens", "Token", 0.9f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("cost", "估算成本", 0.9f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("updated", "最后活动", 1.1f);
            _grid.EmptyText = "还没有会话数据。跑完一个任务，这里会统计 token 消耗和估算成本。";

            var gridCard = new SectionCard("按会话", "Token 降序，最费的一眼看到", Theme.Warn, "∑")
            { Dock = DockStyle.Fill };
            gridCard.Body.Controls.Add(_grid);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            root.Controls.Add(gridCard);
            root.Controls.Add(cards);
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;
            try
            {
                var store = new Core.SessionStore();
                var all = await System.Threading.Tasks.Task.Run(
                () => store.List()).ConfigureAwait(true);

                // 按时间范围过滤。逻辑在 `Session.SessionStats`（纯函数，可单测）——
                // 这边只管把下拉的 SelectedIndex 转成档位。
                int ri = (_range == null || _range.IsDisposed) ? 0 : _range.SelectedIndex;
                var range = Session.SessionStats.RangeFromIndex(ri);
                string rangeText = Session.SessionStats.RangeLabel(range);
                var sessions = Session.SessionStats.FilterByRange(all, range, DateTime.Now);

                // 估算成本用的单价：所有已配置模型的平均值。
                // 会话里没记「用了哪个模型」，所以没法按模型精确计费——
                // 取平均是折中：数量级对，别当账单看。
                double avgPrice = AveragePrice();

                long total = 0, max = 0;
                var rows = new List<Dictionary<string, object>>();
                foreach (var s in sessions)
                {
                    total += s.Tokens;
                    if (s.Tokens > max) max = s.Tokens;
                    // 优先用元信息里的消息数，避免为了一个数字把整个会话文件读进来。
                    // 旧会话没有这个字段（=0），此时才退回 Load。
                    int msgCount = s.MessageCount;
                    if (msgCount == 0)
                    {
                        var sf = await System.Threading.Tasks.Task.Run(
                        () => store.Load(s.Id)).ConfigureAwait(true);
                        msgCount = sf == null ? 0 : sf.Messages.Count;
                    }
                    rows.Add(new Dictionary<string, object>
                    {
                        { "target", s.Target },
                        { "mode", Core.RunModeText.Label(s.Mode) },
                        { "msgs", msgCount },
                        { "tokens", Theme.Tokens(s.Tokens) },
                        { "cost", Theme.Cost(s.Tokens, avgPrice) },
                        { "updated", s.UpdatedAt.ToString("MM-dd HH:mm") },
                    });
                }

                _metric[0].Text = Theme.Tokens(total);
                _metric[1].Text = sessions.Count > 0 ? Theme.Tokens(total / sessions.Count) : "—";
                _metric[2].Text = Theme.Tokens(max);
                string totalCost = Theme.Cost(total, avgPrice);
                _metric[3].Text = totalCost.Length > 0 ? totalCost : "—";

                // 按 token 降序，最费的一眼看到
                rows.Sort((a, b) => ParseTok(b["tokens"].ToString()).CompareTo(ParseTok(a["tokens"].ToString())));
                _grid.Fill(rows);

                _hint.Text = sessions.Count == 0
                    ? (all.Count == 0 ? "还没有会话数据。" : rangeText + "没有会话。换个范围试试。")
                    : rangeText + " · 共 " + sessions.Count + " 个会话"
                      + (sessions.Count != all.Count ? "（全部 " + all.Count + " 个）" : "")
                      + " · 按 Token 降序"
                      + (avgPrice > 0 ? " · 成本按平均单价 ¥" + avgPrice.ToString("G") + "/M 估算" : "");
            }
            catch (Exception ex)
            {
                _hint.Text = "统计失败：" + Theme.OneLine(ex.Message);
            }
            await Task.CompletedTask;
        }

        /// <summary>所有已配置模型的平均单价（没有配置就返回 0）。</summary>
        private static double AveragePrice()
        {
            try
            {
                var reg = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                    new Llm.SecretStore(Core.AppPaths.SecretsFile));
                double sum = 0; int n = 0;
                foreach (var m in reg.Models())
                {
                    if (m.PricePerMillionCny > 0) { sum += m.PricePerMillionCny; n++; }
                }
                reg.Dispose();
                return n > 0 ? sum / n : 0;
            }
            catch { return 0; }
        }

        private static long ParseTok(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            if (s.EndsWith("M")) { double d; return double.TryParse(s.TrimEnd('M'), out d) ? (long)(d * 1e6) : 0; }
            if (s.EndsWith("K")) { double d; return double.TryParse(s.TrimEnd('K'), out d) ? (long)(d * 1e3) : 0; }
            long n;
            return long.TryParse(s, out n) ? n : 0;
        }
    }
}
