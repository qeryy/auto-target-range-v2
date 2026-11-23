// 灵枢桌面版 —— 板块卡（内容分类的基本单元）
//
// 用户要求「板块内容分类、不要全是方框」。一个板块 = 一张软卡：
//   顶部色晕（分类色）+ 圆角图标块 + 板块名 + 一句说明 + 右侧动作
//   下面是 Body：内容随便放（表格/表单/列表），四周留白，没有内边框。
//
// 各页不再自己拼「Label 抬头 + 裸表格」，统一从这里拿，分类感自然一致。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    /// <summary>板块图标块：分类色 16% 的软底 + 同色字符图标，圆角 10。</summary>
    public sealed class HueTile : Control
    {
        public Color Hue { get; set; }
        public string Glyph { get; set; }

        public HueTile()
        {
            Size = new Size(32, 32);
            Hue = Theme.Accent;
            Glyph = "◎";
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            AmbientPanel.PaintBase(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundPath(r, Theme.K(10)))
            using (var b = new SolidBrush(Theme.Mix(Hue, Theme.Surface, 0.16)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, Glyph, Theme.FontHeading, r, Hue,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>带分类抬头的软卡。内容放进 <see cref="Body"/>。</summary>
    public sealed class SectionCard : Card
    {
        private readonly Panel _head;
        private readonly Panel _actions;

        /// <summary>内容区（透明底、四周留白）。Dock=Fill 已设好。</summary>
        public Panel Body { get; private set; }

        public SectionCard(string title, string subtitle, Color hue, string glyph,
            params Control[] actions)
        {
            Tint = hue;
            bool hasSub = !string.IsNullOrEmpty(subtitle);
            // 色晕只罩抬头那一段，到内容区上沿正好渐隐完——
            // 不然不透明的表格/折线会盖住残留色晕，露出一条横向接缝。
            TintHeight = hasSub ? 64 : 52;
            _head = new Panel
            {
                Dock = DockStyle.Top,
                Height = hasSub ? 64 : 52,
                BackColor = Color.Transparent,
            };
            _head.Controls.Add(new HueTile
            {
                Hue = hue,
                Glyph = glyph ?? "◎",
                Location = new Point(18, hasSub ? 16 : 10),
            });
            _head.Controls.Add(new Label
            {
                Text = title,
                Font = Theme.FontHeading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(62, hasSub ? 14 : 16),
                BackColor = Color.Transparent,
            });
            if (hasSub)
            {
                _head.Controls.Add(new Label
                {
                    Text = subtitle,
                    Font = Theme.FontSmall,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Location = new Point(63, 38),
                    BackColor = Color.Transparent,
                });
            }

            _actions = new Panel
            {
                Dock = DockStyle.Right,
                Width = 8,
                BackColor = Color.Transparent,
            };
            if (actions != null && actions.Length > 0)
            {
                int w = 8;
                foreach (var a in actions) w += a.Width + 8;
                _actions.Width = w;
                int x = 8;
                foreach (var a in actions)
                {
                    a.Location = new Point(x, (_head.Height - a.Height) / 2);
                    _actions.Controls.Add(a);
                    x += a.Width + 8;
                }
            }
            _head.Controls.Add(_actions);

            Body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(18, 2, 18, 16),
            };
            Controls.Add(Body);
            Controls.Add(_head);
        }
    }
}