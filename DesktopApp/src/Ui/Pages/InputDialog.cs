// 灵枢桌面版 —— 通用输入对话框
//
// 重命名会话、给项目改名这类「只要一个字符串」的场景用它。
// WinForms 没有内置的 InputBox（VB 有，但引 Microsoft.VisualBasic 不值得）。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpiritScout.Ui.Pages
{
    public sealed class InputDialog : Form
    {
        public string Value { get; private set; }

        private readonly TextBox _box;
        private readonly Label _error;

        public InputDialog(string title, string label, string initial)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            Text = title;
            ClientSize = new Size(480, 176);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;
            Value = initial ?? "";

            Controls.Add(new Label
            {
                Text = label ?? "",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                Location = new Point(20, 18),
            });

            _box = Theme.Input();
            _box.Text = Value;
            _box.Location = new Point(20, 44);
            _box.Width = ClientSize.Width - 40;
            _box.SelectAll();
            Controls.Add(_box);

            _error = new Label
            {
                Location = new Point(20, 78),
                Size = new Size(ClientSize.Width - 40, 20),
                ForeColor = Theme.Danger,
                Font = Theme.FontSmall,
            };
            Controls.Add(_error);

            var ok = Theme.Primary("确定", 92, 34);
            ok.Location = new Point(ClientSize.Width - 116, ClientSize.Height - 48);
            ok.Click += (s, e) => Submit();
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 34);
            cancel.Location = new Point(ClientSize.Width - 208, ClientSize.Height - 48);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Submit()
        {
            string v = (_box.Text ?? "").Trim();
            if (v.Length == 0)
            {
                _error.Text = "不能为空。";
                _box.Focus();
                return;
            }
            Value = v;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
