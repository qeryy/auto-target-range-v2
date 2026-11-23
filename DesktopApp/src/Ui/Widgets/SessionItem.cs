// 灵枢桌面版 —— 侧栏会话项（对齐 web #sess-list 的 .sess）
//
// 一行一个会话：目标名（截断）+ 模式小字；悬停右侧出现「×」可删。
// 选中（当前打开的会话）= accent 软底 + 亮蓝字，和导航选中态统一。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class SessionItem : Button
    {
        private bool _hover;
        private bool _active;
        private int _mouseX;
        private int _mouseY;

        public string SessionId { get; set; }
        public string Target { get; set; }
        public string ModeLabel { get; set; }

        public event EventHandler OpenRequested;
        public event EventHandler DeleteRequested;

        public SessionItem()
        {
            SessionId = "";
            Target = "";
            ModeLabel = "";
            Height = 52;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontBody;
            BackColor = Theme.Surface;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public void SetActive(bool active)
        {
            if (_active != active) { _active = active; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e) { _mouseX = e.X; _mouseY = e.Y; base.OnMouseMove(e); }

        protected override void OnClick(EventArgs e)
        {
            // 右上角 24x24 的删除块（悬停/选中才画），其余区域都是「打开」
            if (_hover && _mouseX > Width - Theme.K(34) && _mouseY < Theme.K(32))
            {
                if (DeleteRequested != null) DeleteRequested(this, EventArgs.Empty);
            }
            else
            {
                if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 同 NavItem：这条路径在 Button 派生类上不会被调用，铺底在 OnPaint 里做。
            using (var b = new SolidBrush(Theme.Sidebar))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 回收的双缓冲里留着别的同尺寸控件的像素，先刷实底再画，杜绝会话行残影。
            using (var b = new SolidBrush(Theme.Sidebar))
                g.FillRectangle(b, ClientRectangle);

            if (Width < Theme.K(20) || Height < Theme.K(12)) return;

            // 软块：圆角 10，选中 = accent 软底 + 30% accent 描边（web .sess-item.on）
            var pill = new Rectangle(Theme.K(8), Theme.K(2), Width - Theme.K(16), Height - Theme.K(4));
            // web 的 .sess-item 是 background: var(--surface-2) 悬停 + accent-soft 选中。
            // 混色基数用 AccentSoft（深 15% / 浅 9%），别用 Sidebar ——
            // Sidebar 浅色是 #f0f2f5，Mix(Accent, Sidebar, 0.15) 调出的是几乎看不见的淡蓝。
            Color fill = _active ? Theme.AccentSoft
                       : _hover ? Theme.Mix(Theme.Surface, Theme.SurfaceAlt, 0.5)
                       : Color.Empty;
            if (!fill.IsEmpty)
            {
                using (var path = Theme.RoundPath(pill, Theme.K(10)))
                using (var b = new SolidBrush(fill))
                    g.FillPath(b, path);
                if (_active)
                {
                    using (var path = Theme.RoundPath(pill, Theme.K(10)))
                    using (var pen = new Pen(Theme.Mix(Theme.Accent, Theme.Line, 0.45), Theme.Kf(1)))
                    {
                        pen.Alignment = PenAlignment.Inset;
                        g.DrawPath(pen, path);
                    }
                }
            }

            // 模式色点：一眼分清 SRC / CTF / 竞赛（对齐 web .sess-item .t 的 gap 7）
            Color hue = ModeHue();
            using (var b = new SolidBrush(hue))
                g.FillEllipse(b, Theme.K(18), Theme.K(13), Theme.K(6), Theme.K(6));

            Color fore = _active ? Theme.AccentInk : (_hover ? Theme.Text : Theme.Muted);
            int right = Width - Theme.K(_hover || _active ? 38 : 14);
            var line1 = new Rectangle(Theme.K(31), Theme.K(7), right - Theme.K(31), Theme.K(18));
            TextRenderer.DrawText(g, Target, Theme.FontSession, line1, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            var line2 = new Rectangle(Theme.K(31), Theme.K(27), right - Theme.K(31), Theme.K(16));
            TextRenderer.DrawText(g, ModeLabel, Theme.FontTiny, line2,
                _active ? Theme.Mix(Theme.AccentInk, Theme.Muted, 0.5) : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            // 悬停/选中时右上角的删除块：软底 + 暗圈 + ×（web .sess-item .ops）
            if (_hover || _active)
            {
                var ops = new Rectangle(Width - Theme.K(32), Theme.K(8), Theme.K(22), Theme.K(22));
                using (var path = Theme.RoundPath(ops, Theme.K(7)))
                using (var b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Sidebar, 0.7)))
                    g.FillPath(b, path);
                using (var path = Theme.RoundPath(ops, Theme.K(7)))
                using (var pen = new Pen(Theme.LineDark, Theme.Kf(1)))
                {
                    pen.Alignment = PenAlignment.Inset;
                    g.DrawPath(pen, path);
                }
                int cx = ops.X + ops.Width / 2, cy = ops.Y + ops.Height / 2;
                float arm = Theme.Kf(3.5f);
                using (var pen = new Pen(_hover ? Theme.Text : Theme.Muted, Theme.Kf(1.5f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, cx - arm, cy - arm, cx + arm, cy + arm);
                    g.DrawLine(pen, cx + arm, cy - arm, cx - arm, cy + arm);
                }
            }
        }

        private Color ModeHue()
        {
            if (ModeLabel != null && ModeLabel.IndexOf("CTF", StringComparison.OrdinalIgnoreCase) >= 0)
                return Theme.Ok;
            if (ModeLabel != null && ModeLabel.Contains("竞赛")) return Theme.Violet;
            return Theme.Accent;
        }
    }
}