// 灵枢桌面版 —— 页面：任务
//
// 对应 web 的 view-home：列出所有会话/任务，支持新建、刷新、点进详情。
// 数据来自 GET /api/sessions，新建走 POST /api/sessions。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Native.Pages
{
    public sealed class HomePage : IPage
    {
        public string Id { get { return "home"; } }
        public string Title { get { return "任务"; } }
        public PageGroup Group { get { return PageGroup.Main; } }

        private AppContext _ctx;
        private DataGrid _grid;
        private Label _hint;
        private Button _refresh;

        public HomePage(AppContext ctx)
        {
            _ctx = ctx;
        }

        public Control CreateView()
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = UiKit.Bg, Padding = new Padding(28, 20, 28, 16) };

            var newBtn = UiKit.PrimaryButton("+ 新任务", 108);
            newBtn.Click += async (s, e) => await NewTaskAsync().ConfigureAwait(true);

            _refresh = UiKit.GhostButton("刷新", 76);
            _refresh.Click += async (s, e) => await RefreshAsync().ConfigureAwait(true);

            var head = UiKit.PageHeader("任务",
                "每个任务是一个独立的解题/挖洞会话，点一行查看详情", newBtn, _refresh);

            _grid = new DataGrid { Dock = DockStyle.Fill };
            _grid.AddColumn("target", "目标", 2.2f);
            _grid.AddColumn("mode", "模式", 0.8f);
            _grid.AddColumn("phase", "状态", 1.0f);
            _grid.AddColumn("tokens", "Token", 0.8f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("created", "创建时间", 1.2f);
            _grid.AddColumn("sid", "会话", 2.0f);
            _grid.CellDoubleClick += async (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var sid = Convert.ToString(_grid.Rows[e.RowIndex].Cells["sid"].Value);
                if (!string.IsNullOrEmpty(sid))
                    MessageBox.Show(root, "会话详情页还没搬过来。\n\n会话：" + sid,
                        "灵枢", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await Task.CompletedTask;
            };

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                ForeColor = UiKit.Muted,
                Font = UiKit.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "",
            };

            root.Controls.Add(_grid);
            root.Controls.Add(_hint);
            root.Controls.Add(head);
            return root;
        }

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;
            _hint.Text = "正在加载…";
            try
            {
                string json = await _ctx.Api.GetAsync("/api/sessions").ConfigureAwait(true);
                var doc = SimpleJson.Parse(json);

                // 后端两种形态都兼容：裸数组，或 {"sessions":[...]}
                List<object> arr = SimpleJson.AsList(doc);
                if (arr == null)
                {
                    var d = SimpleJson.AsDict(doc);
                    arr = SimpleJson.AsList(d != null && d.ContainsKey("sessions") ? d["sessions"] : null);
                }

                var rows = new List<Dictionary<string, object>>();
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var d = SimpleJson.AsDict(item);
                        if (d == null) continue;
                        rows.Add(new Dictionary<string, object>
                        {
                            { "target", SimpleJson.Str(d, "target") },
                            { "mode", SimpleJson.Str(d, "mode") },
                            { "phase", SimpleJson.Str(d, "phase") },
                            { "tokens", SimpleJson.Str(d, "tokens") },
                            { "created", SimpleJson.Str(d, "created_at") },
                            { "sid", SimpleJson.Str(d, "session_id") },
                        });
                    }
                }

                _grid.Fill(rows);
                _grid.ColorizeCells("phase", v =>
                {
                    if (v == "completed") return UiKit.Ok;
                    if (v == "executing" || v == "running") return UiKit.Accent;
                    if (v == "failed" || v == "stopped") return UiKit.Danger;
                    if (v == "paused") return UiKit.Warn;
                    return null;
                });

                _hint.Text = rows.Count == 0
                    ? "还没有任务。点右上角「+ 新任务」开始。"
                    : "共 " + rows.Count + " 个任务";
            }
            catch (Exception ex)
            {
                _hint.Text = "加载失败：" + UiKit.OneLine(ex.Message);
            }
        }

        private async Task NewTaskAsync()
        {
            using (var dlg = new NewTaskDialog(_ctx.Api))
            {
                if (dlg.ShowModal(_ctx.Shell))
                    await RefreshAsync().ConfigureAwait(true);
            }
        }
    }
}
