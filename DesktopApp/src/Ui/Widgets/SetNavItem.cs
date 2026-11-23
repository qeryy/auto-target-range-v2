// 灵枢桌面版 —— 设置外壳的二级导航项
//
// 对齐 web 版 `polish.css` 的 `.set-nav button`：
//   font-size: 13px; padding: 8px 10px; border-radius: 8px;
//
// 和主导航 `NavItem` 不是同一个东西：
//   · 主导航 36px 高、字号大一号、胶囊更宽；
//   · 设置二级导航 30px 高、字号小一号、胶囊更窄 —— 它是「页内」不是「应用级」。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    /// <summary>设置外壳的二级导航项：小字号、窄胶囊。</summary>
    public sealed class SetNavItem : Control
    {
        private bool _active;
        private bool _hover;

        public SetNavItem()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            Font = Theme.FontSmall;                       // 11.3px（web: 13px 里偏小一档）
            ForeColor = Theme.Muted;
            BackColor = Theme.Bg;
            Height = Theme.K(30);
            Cursor = Cursors.Hand;
        }

        /// <summary>选中态（左侧色标 + 微亮底）。</summary>
        public void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Width < Theme.K(8) || Height < Theme.K(6)) return;

            // 胶囊底：选中用 accent-soft（蓝底微亮），悬停用 surface，常态透明
            var pill = new Rectangle(Theme.K(6), Theme.K(2), Width - Theme.K(12), Height - Theme.K(4));
            if (pill.Width > Theme.K(4) && pill.Height > Theme.K(4))
            {
                Color fill = _active
                    // web 的 .set-nav button.on 是 accent-soft 整底亮
                    // （accent 15% 混 surface），不是侧栏那种色标。
                    // Mix(Accent, Surface, 0.15) ≈ #1C2B39
                    ? Theme.Mix(Theme.Accent, Theme.Surface, 0.15)
                    : _hover
                        ? Theme.Mix(Theme.Surface, Theme.SurfaceAlt, 0.4)
                        : BackColor;
                if (fill != BackColor || _active || _hover)
                {
                    using (var path = Theme.RoundPath(pill, Theme.RadiusSm))
                    using (var brush = new SolidBrush(fill))
                        g.FillPath(brush, path);
                }
            }


            // 文字
            var tr = new Rectangle(Theme.K(16), 0, Width - Theme.K(24), Height);
            TextRenderer.DrawText(g, Text, Font, tr,
                _active ? Theme.AccentInk : (_hover ? Theme.Text : Theme.Muted),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}
