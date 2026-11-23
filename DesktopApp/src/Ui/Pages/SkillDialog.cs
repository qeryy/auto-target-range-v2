// 灵枢桌面版 —— 技能编辑对话框
//
// 写一条技能要填四样东西：id、领域、一句话说明、正文（Markdown）。
// 正文是给模型看的方法论，所以给个大文本框。

using System;
using System.Drawing;
using System.Windows.Forms;
using SpiritScout.Knowledge;

namespace SpiritScout.Ui.Pages
{
    public sealed class SkillDialog : Form
    {
        public SkillEntry Result { get; private set; }

        private readonly SkillEntry _src;
        private readonly bool _isNew;
        private TextBox _id;
        private TextBox _domain;
        private TextBox _desc;
        private TextBox _body;
        private Label _error;

        public SkillDialog(SkillEntry existing)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            _isNew = existing == null;
            _src = existing ?? new SkillEntry();

            Text = _isNew ? "新建技能" : "编辑技能";
            ClientSize = new Size(760, 620);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;

            Build();
        }

        private void Build()
        {
            // 标签列宽要容得下最宽的「一句话说明 *」：字号整体加两个字号后它量到 88 设计像素，
            // 原来的 88 列宽正好把文字顶进输入框（压字探针抓到的就是这个）。
            int x = 22, y = 16, fx = 118;
            int fw = ClientSize.Width - fx - 30;

            Row("技能 ID *", x, y);
            _id = Theme.Input();
            _id.Text = _src.Id;
            _id.Location = new Point(fx, y);
            _id.Width = 220;
            Controls.Add(_id);
            Controls.Add(Hint("短的英文标识，如 my-sqli-trick；与内置同名会覆盖它", fx + 228, y + 4));
            y += 38;

            Row("领域", x, y);
            _domain = Theme.Input();
            _domain.Text = _src.Domain;
            _domain.Location = new Point(fx, y);
            _domain.Width = 220;
            Controls.Add(_domain);
            Controls.Add(Hint("如 web / reverse / ctf；留空算「未分类」", fx + 228, y + 4));
            y += 38;

            Row("一句话说明 *", x, y);
            _desc = Theme.Input();
            _desc.Text = _src.Description;
            _desc.Location = new Point(fx, y);
            _desc.Width = fw;
            Controls.Add(_desc);
            y += 38;

            Controls.Add(new Label
            {
                Text = "正文（Markdown；写清楚什么场景用什么手法、有哪些坑）",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                Location = new Point(x, y),
            });
            y += 22;

            _body = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = true,
                AcceptsReturn = true,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.FontMono,
                Location = new Point(x, y),
                Size = new Size(ClientSize.Width - x * 2, ClientSize.Height - y - 96),
            };
            _body.Text = _src.Body;
            Controls.Add(_body);
            y += _body.Height + 6;

            _error = new Label
            {
                Location = new Point(x, y),
                Size = new Size(ClientSize.Width - 44, 22),
                ForeColor = Theme.Danger,
                Font = Theme.FontSmall,
            };
            Controls.Add(_error);

            var ok = Theme.Primary("保存", 104, 36);
            ok.Location = new Point(ClientSize.Width - 134, ClientSize.Height - 50);
            ok.Click += (s, e) => Submit();
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 36);
            cancel.Location = new Point(ClientSize.Width - 230, ClientSize.Height - 50);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Row(string text, int x, int y)
        {
            Controls.Add(Theme.FormLabel(text, x, y));
        }

        private static Label Hint(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
            };
        }

        private void Submit()
        {
            _error.Text = "";
            string id = (_id.Text ?? "").Trim();
            string desc = (_desc.Text ?? "").Trim();

            if (id.Length == 0) { _error.Text = "技能 ID 不能为空。"; _id.Focus(); return; }
            if (id.Length > 64) { _error.Text = "技能 ID 太长了（最多 64 字符）。"; _id.Focus(); return; }
            foreach (char c in id)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                {
                    _error.Text = "技能 ID 只能用字母、数字、- _ .";
                    _id.Focus();
                    return;
                }
            }
            if (desc.Length == 0) { _error.Text = "一句话说明不能为空。"; _desc.Focus(); return; }

            Result = new SkillEntry
            {
                Id = id,
                Domain = (_domain.Text ?? "").Trim(),
                Description = desc,
                Body = _body.Text ?? "",
                Tags = _src.Tags,
                Source = "user",
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
