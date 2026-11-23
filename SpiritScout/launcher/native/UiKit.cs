// 灵枢桌面版（原生）—— 界面工具箱
//
// 目的有两个：
//   1. **好看**：把配色、字号、圆角、间距收在一处，所有页面自动一致。
//      散在各页里手写 Color.FromArgb 的结果就是「同屏三种灰」。
//   2. **好维护**：按钮、卡片、表格、表单行都从这里拿，改一次全局生效。
//
// 配色跟着 web 端的深色主题走（bg #0d1117 / 面板 #161b22 / 主色 #2f81f7），
// 这样从浏览器切到桌面版视觉上是同一个产品。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Native
{
    /// <summary>全局主题与常用控件工厂。</summary>
    public static class UiKit
    {
        // ---------- 配色（与 web 端一致） ----------
        public static readonly Color Bg = Color.FromArgb(13, 17, 23);
        public static readonly Color Surface = Color.FromArgb(22, 27, 34);
        public static readonly Color SurfaceHover = Color.FromArgb(33, 38, 45);
        public static readonly Color Accent = Color.FromArgb(47, 129, 247);
        public static readonly Color AccentHover = Color.FromArgb(56, 139, 253);
        public static readonly Color Text = Color.FromArgb(230, 237, 243);
        public static readonly Color Muted = Color.FromArgb(139, 148, 158);
        public static readonly Color Line = Color.FromArgb(48, 54, 61);
        public static readonly Color Ok = Color.FromArgb(63, 185, 80);
        public static readonly Color Warn = Color.FromArgb(210, 153, 34);
        public static readonly Color Danger = Color.FromArgb(248, 81, 73);

        // ---------- 字体 ----------
        public static readonly Font FontBody = new Font("Microsoft YaHei UI", 9.5F);
        public static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8.5F);
        public static readonly Font FontTitle = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold);
        public static readonly Font FontHeading = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        public static readonly Font FontMono = new Font("Consolas", 9F);

        /// <summary>页面标题（每页顶部那行大字 + 一行说明）。</summary>
        public static Panel PageHeader(string title, string subtitle, params Control[] actions)
        {
            var head = new Panel { Dock = DockStyle.Top, Height = subtitle == null ? 54 : 68, BackColor = Bg };

            var lbl = new Label
            {
                Text = title,
                Font = FontTitle,
                ForeColor = Text,
                AutoSize = true,
                Location = new Point(0, 0),
            };
            head.Controls.Add(lbl);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = new Label
                {
                    Text = subtitle,
                    Font = FontSmall,
                    ForeColor = Muted,
                    AutoSize = true,
                    Location = new Point(2, 36),
                };
                head.Controls.Add(sub);
            }

            // 右侧动作按钮：从右往左摆
            int right = 0;
            for (int i = actions.Length - 1; i >= 0; i--)
            {
                var btn = actions[i];
                btn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                head.Controls.Add(btn);
                right += btn.Width + 8;
                btn.Location = new Point(head.Width - right, 6);
                head.Resize += (s, e) =>
                {
                    int r = 0;
                    for (int j = actions.Length - 1; j >= 0; j--)
                    {
                        r += actions[j].Width + 8;
                        actions[j].Location = new Point(head.Width - r, 6);
                    }
                };
            }
            return head;
        }

        /// <summary>主按钮（蓝色实心）。</summary>
        public static Button PrimaryButton(string text, int width = 104, int height = 34)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                BackColor = Accent,
                ForeColor = Color.White,
                Font = FontBody,
                Cursor = Cursors.Hand,
                TabStop = true,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            return b;
        }

        /// <summary>次按钮（描边）。</summary>
        public static Button GhostButton(string text, int width = 88, int height = 34)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = Text,
                Font = FontBody,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = SurfaceHover;
            return b;
        }

        /// <summary>危险按钮（删除类操作）。</summary>
        public static Button DangerButton(string text, int width = 88, int height = 34)
        {
            var b = GhostButton(text, width, height);
            b.ForeColor = Danger;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 40, 38);
            return b;
        }

        /// <summary>卡片容器：圆角描边的一块面板。</summary>
        public static Panel Card(int padding = 16)
        {
            var p = new Panel
            {
                BackColor = Surface,
                Padding = new Padding(padding),
                Margin = new Padding(0, 0, 0, 12),
            };
            return p;
        }

        /// <summary>带边框的单行输入。</summary>
        public static TextBox Input(string placeholder = "", bool multiline = false, int height = 26)
        {
            var t = new TextBox
            {
                BackColor = Bg,
                ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = FontBody,
                Multiline = multiline,
            };
            if (multiline)
            {
                t.Height = height;
                t.ScrollBars = ScrollBars.Vertical;
            }
            // WinForms 没有原生 placeholder；空值时用灰色提示
            if (!string.IsNullOrEmpty(placeholder) && !multiline)
            {
                t.Text = placeholder;
                t.ForeColor = Muted;
                t.GotFocus += (s, e) =>
                {
                    if (t.Text == placeholder && t.ForeColor == Muted)
                    {
                        t.Text = "";
                        t.ForeColor = Text;
                    }
                };
                t.LostFocus += (s, e) =>
                {
                    if (string.IsNullOrEmpty(t.Text))
                    {
                        t.Text = placeholder;
                        t.ForeColor = Muted;
                    }
                };
            }
            return t;
        }

        public static ComboBox Select(params string[] items)
        {
            var c = new ComboBox
            {
                BackColor = Bg,
                ForeColor = Text,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FontBody,
            };
            if (items != null && items.Length > 0)
            {
                c.Items.AddRange(items);
                c.SelectedIndex = 0;
            }
            return c;
        }

        /// <summary>一行「标签 + 控件」，用于表单。</summary>
        public static Panel Field(string label, Control control, int labelWidth = 96)
        {
            var row = new Panel { Height = Math.Max(32, control.Height + 8), BackColor = Bg };
            var lbl = new Label
            {
                Text = label,
                ForeColor = Muted,
                Font = FontSmall,
                AutoSize = false,
                Width = labelWidth,
                Height = control.Height,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(0, 0),
            };
            control.Location = new Point(labelWidth, 0);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            control.Width = Math.Max(120, row.Width - labelWidth);
            row.Controls.Add(lbl);
            row.Controls.Add(control);
            row.Resize += (s, e) => { control.Width = Math.Max(120, row.Width - labelWidth); };
            return row;
        }

        /// <summary>小徽标（状态 pill）。</summary>
        public static Label Badge(string text, Color color)
        {
            var l = new Label
            {
                Text = " " + text + " ",
                Font = FontSmall,
                ForeColor = color,
                BackColor = Color.FromArgb(28, 33, 40),
                AutoSize = false,
                Height = 20,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(4, 0, 4, 0),
            };
            l.Width = TextRenderer.MeasureText(l.Text, FontSmall).Width + 10;
            return l;
        }

        /// <summary>空状态提示（还没有数据时显示，比一个空白表格友好）。</summary>
        public static Label Empty(string message)
        {
            return new Label
            {
                Text = message,
                ForeColor = Muted,
                Font = FontBody,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
            };
        }

        /// <summary>把一段错误文本压成一行（状态栏用）。</summary>
        public static string OneLine(string s, int max = 160)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    /// <summary>
    /// 圆角面板。WinForms 原生 Panel 是直角的；重写 OnPaint 画圆角底，
    /// 视觉上与 web 端的卡片一致。
    /// </summary>
    public sealed class RoundPanel : Panel
    {
        public int Radius { get; set; }
        public Color BorderColor { get; set; }

        public RoundPanel()
        {
            Radius = 10;
            BorderColor = UiKit.Line;
            DoubleBuffered = true;
            BackColor = UiKit.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(rect, Radius))
            using (var fill = new SolidBrush(BackColor))
            using (var pen = new Pen(BorderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
