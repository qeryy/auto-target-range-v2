// 灵枢桌面版（原生）—— 新建任务对话框
//
// 对应 Web 端的「+ 新任务」：目标、模式、任务描述、Flag 格式。
// 提交后直接调 /api/sessions，成功后关闭对话框。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Native
{
    public sealed class NewTaskDialog : Form
    {
        private readonly SpiritApi _api;
        private TextBox _target;
        private ComboBox _mode;
        private TextBox _mission;
        private TextBox _flagFormat;
        private Label _error;
        private Button _submit;

        private static readonly Color BgColor = Color.FromArgb(13, 17, 23);
        private static readonly Color PanelColor = Color.FromArgb(22, 27, 34);
        private static readonly Color AccentColor = Color.FromArgb(47, 129, 247);
        private static readonly Color TextColor = Color.FromArgb(230, 237, 243);
        private static readonly Color MutedColor = Color.FromArgb(139, 148, 158);
        private static readonly Color LineColor = Color.FromArgb(48, 54, 61);

        public NewTaskDialog(SpiritApi api)
        {
            _api = api;
            Text = "新建任务";
            ClientSize = new Size(640, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = BgColor;
            ForeColor = TextColor;
            Font = new Font("Microsoft YaHei UI", 9.5F);

            BuildUi();
        }

        private void BuildUi()
        {
            int y = 24;
            int labelW = 96;
            int fieldX = 120;
            int fieldW = ClientSize.Width - fieldX - 32;

            AddLabel("目标 *", 24, y + 6);
            _target = AddTextBox(fieldX, y, fieldW, "例：https://example.com 或 challenge-1");
            y += 44;

            AddLabel("模式 *", 24, y + 6);
            _mode = new ComboBox
            {
                Location = new Point(fieldX, y),
                Size = new Size(200, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = PanelColor,
                ForeColor = TextColor,
                FlatStyle = FlatStyle.Flat,
            };
            _mode.Items.AddRange(new object[] { "ctf", "src", "competition" });
            _mode.SelectedIndex = 0;
            Controls.Add(_mode);
            y += 44;

            AddLabel("任务描述", 24, y + 6);
            _mission = new TextBox
            {
                Location = new Point(fieldX, y),
                Size = new Size(fieldW, 140),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = PanelColor,
                ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(_mission);
            y += 156;

            AddLabel("Flag 格式", 24, y + 6);
            _flagFormat = AddTextBox(fieldX, y, fieldW, @"flag\{[a-z0-9_]+\}");
            y += 44;

            _error = new Label
            {
                Location = new Point(24, y),
                Size = new Size(ClientSize.Width - 48, 40),
                ForeColor = Color.FromArgb(248, 81, 73),
                Text = "",
            };
            Controls.Add(_error);

            _submit = new Button
            {
                Text = "创建任务",
                Size = new Size(120, 36),
                Location = new Point(ClientSize.Width - 152, ClientSize.Height - 64),
                FlatStyle = FlatStyle.Flat,
                BackColor = AccentColor,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
            };
            _submit.FlatAppearance.BorderSize = 0;
            _submit.Click += async (s, e) => await SubmitAsync().ConfigureAwait(true);

            var cancel = new Button
            {
                Text = "取消",
                Size = new Size(88, 36),
                Location = new Point(ClientSize.Width - 252, ClientSize.Height - 64),
                FlatStyle = FlatStyle.Flat,
                BackColor = PanelColor,
                ForeColor = TextColor,
                Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel,
            };
            cancel.FlatAppearance.BorderColor = LineColor;

            Controls.Add(_submit);
            Controls.Add(cancel);
            AcceptButton = _submit;
            CancelButton = cancel;
        }

        private void AddLabel(string text, int x, int y)
        {
            var lbl = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = MutedColor,
            };
            Controls.Add(lbl);
        }

        private TextBox AddTextBox(int x, int y, int w, string placeholder)
        {
            var tb = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 26),
                BackColor = PanelColor,
                ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle,
                Text = "",
            };
            Controls.Add(tb);
            return tb;
        }

        private async Task SubmitAsync()
        {
            _error.Text = "";
            string target = (_target.Text ?? "").Trim();
            if (target.Length == 0)
            {
                _error.Text = "目标不能为空。";
                _target.Focus();
                return;
            }

            _submit.Enabled = false;
            _submit.Text = "创建中…";
            try
            {
                var body = new Dictionary<string, object>
                {
                    { "target", target },
                    { "mode", _mode.SelectedItem.ToString() },
                    { "mission", (_mission.Text ?? "").Trim() },
                    { "flag_format", (_flagFormat.Text ?? "").Trim() },
                };
                string json = SimpleJson.Stringify(body);
                string resp = await _api.PostAsync("/api/sessions", json).ConfigureAwait(true);

                var doc = SimpleJson.AsDict(SimpleJson.Parse(resp));
                string sid = SimpleJson.Str(doc, "session_id");
                if (string.IsNullOrEmpty(sid))
                {
                    _error.Text = "服务端没有返回 session_id，响应：" + Truncate(resp, 200);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _error.Text = ex.Message;
            }
            finally
            {
                _submit.Enabled = true;
                _submit.Text = "创建任务";
            }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /// <summary>同步显示对话框；返回 true 表示创建成功。</summary>
        public bool ShowModal(IWin32Window owner)
        {
            return ShowDialog(owner) == DialogResult.OK;
        }
    }
}
