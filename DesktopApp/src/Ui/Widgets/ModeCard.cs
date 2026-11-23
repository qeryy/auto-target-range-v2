// 灵枢桌面版 —— 首页模式卡（对齐 web .home-mode-card）
//
// 三选一：SRC 渗透测试 / CTF 解题 / 智能体竞赛。
// 选中态 = accent 描边(2px) + accent 7% 软底 + 右侧实心圆点；
// 未选中 = 暗线描边 + Surface 底 + 空心圆。悬停描边提亮。
// 图标用 GDI 按 web 的 SVG 路径画（盾/旗/组织），不引资源文件。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public enum ModeKind { Src, Ctf, Competition }

    public sealed class ModeCard : Button
    {
        private bool _hover;
        private bool _selected;

        public ModeKind Kind { get; set; }
        public string Caption { get; set; }
        public string Sub { get; set; }

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        public ModeCard()
        {
            Kind = ModeKind.Ctf;
            Caption = "";
            Sub = "";
            Height = 96;
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
            AccessibleRole = AccessibleRole.RadioButton;
        }

        /// <summary>每种模式一个标识色：SRC 蓝 / CTF 绿 / 竞赛紫。图标块、选中描边、圆点共用。</summary>
        public static Color HueOf(ModeKind kind)
        {
            if (kind == ModeKind.Src) return Theme.Accent;
            if (kind == ModeKind.Ctf) return Theme.Ok;
            return Theme.Violet;
        }

        // 圆角外的四角露父级氛围背景
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            AmbientPanel.PaintBase(this, e.Graphics);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (Width < Theme.K(40) || Height < Theme.K(40)) return;

            // 同 FlatButton：Button 派生类不走 OnPaintBackground，投影留白的那圈
            // 像素必须在 OnPaint 里先铺好，否则会透出回收缓冲里的旧内容。
            AmbientPanel.PaintBase(this, g);

            Color hue = HueOf(Kind);
            // 悬停/选中时卡片「浮」起来：留 4px 给投影
            int pad = Theme.K((_hover || _selected) ? 4 : 2);
            var rect = new Rectangle(pad, pad, Width - 1 - pad * 2, Height - 1 - pad * 2);
            if (_hover || _selected)
                Theme.DrawShadow(g, rect, Theme.K(Theme.RadiusLg), pad, _selected ? 70 : 50);

            Color fill = _selected ? Theme.Mix(hue, Theme.Surface, 0.10)
                       : _hover ? Theme.Mix(Theme.Hover, Theme.Surface, 0.5)
                       : Theme.Surface;
            using (var path = Theme.RoundPath(rect, Theme.K(Theme.RadiusLg)))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, path);

            Color stroke = _selected ? hue : (_hover ? Theme.Line : Theme.LineDark);
            using (var pen = new Pen(stroke, Theme.Kf(_selected ? 1.6f : 1f)))
            {
                pen.Alignment = PenAlignment.Inset;
                using (var path = Theme.RoundPath(rect, Theme.K(Theme.RadiusLg)))
                    g.DrawPath(pen, path);
            }

            // 图标块：模式色 16% 软底圆角方块 + 模式色线图标
            var tile = new Rectangle(rect.X + Theme.K(14), rect.Y + Theme.K(14), Theme.K(40), Theme.K(40));
            using (var path = Theme.RoundPath(tile, Theme.K(10)))
            using (var b = new SolidBrush(Theme.Mix(hue, Theme.Surface, _selected ? 0.22 : 0.14)))
                g.FillPath(b, path);
            DrawIcon(g, new Rectangle(tile.X + Theme.K(9), tile.Y + Theme.K(9), Theme.K(22), Theme.K(22)), _selected ? hue : Theme.Mix(hue, Theme.Text, 0.35));

            // 标题 + 副标题
            int tx = tile.Right + Theme.K(12);
            TextRenderer.DrawText(g, Caption, Theme.FontHeading, new Point(tx, rect.Y + Theme.K(16)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Sub, Theme.FontSmall, new Point(tx, rect.Y + Theme.K(44)), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            // 右上选择圆点
            int cx = rect.Right - Theme.K(18), cy = rect.Y + Theme.K(18), r = Theme.K(7);
            using (var pen = new Pen(_selected ? hue : Theme.Line, Theme.Kf(_selected ? 4f : 1.5f)))
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            if (_selected)
                using (var b = new SolidBrush(hue))
                    g.FillEllipse(b, cx - Theme.K(3), cy - Theme.K(3), Theme.K(6), Theme.K(6));
        }

        private void DrawIcon(Graphics g, Rectangle box, Color color)
        {
            float k = box.Width / 24f;
            Func<float, float> X = v => box.Left + v * k;
            Func<float, float> Y = v => box.Top + v * k;
            using (var pen = new Pen(color, Math.Max(Theme.Kf(1.5f), 1.8f * k)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (Kind == ModeKind.Src)
                {
                    // 盾 + 勾
                    using (var p = new GraphicsPath())
                    {
                        p.AddLine(X(12), Y(3), X(20), Y(6));
                        p.AddLine(X(20), Y(6), X(20), Y(11));
                        p.AddBezier(X(20), Y(11), X(20), Y(16), X(16), Y(19.5f), X(12), Y(21));
                        p.AddBezier(X(12), Y(21), X(8), Y(19.5f), X(4), Y(16), X(4), Y(11));
                        p.AddLine(X(4), Y(11), X(4), Y(6));
                        p.CloseFigure();
                        g.DrawPath(pen, p);
                    }
                    g.DrawLine(pen, X(9), Y(11.5f), X(11.2f), Y(13.8f));
                    g.DrawLine(pen, X(11.2f), Y(13.8f), X(15), Y(9.6f));
                }
                else if (Kind == ModeKind.Ctf)
                {
                    // 旗
                    g.DrawLine(pen, X(6), Y(21), X(6), Y(3.5f));
                    using (var p = new GraphicsPath())
                    {
                        p.AddLine(X(6), Y(4.5f), X(18), Y(4.5f));
                        p.AddLine(X(18), Y(4.5f), X(15.5f), Y(8.5f));
                        p.AddLine(X(15.5f), Y(8.5f), X(18), Y(12.5f));
                        p.AddLine(X(18), Y(12.5f), X(6), Y(12.5f));
                        g.DrawPath(pen, p);
                    }
                }
                else
                {
                    // 组织：顶节点 + 两子节点（宽高用相对值 5*k，不能用绝对坐标 X(5)）
                    g.DrawRectangle(pen, X(9.5f), Y(3), 5 * k, 4.5f * k);
                    g.DrawRectangle(pen, X(3.5f), Y(16), 5 * k, 4.5f * k);
                    g.DrawRectangle(pen, X(15.5f), Y(16), 5 * k, 4.5f * k);
                    g.DrawLine(pen, X(12), Y(7.5f), X(12), Y(11.5f));
                    g.DrawLine(pen, X(6), Y(11.5f), X(18), Y(11.5f));
                    g.DrawLine(pen, X(6), Y(11.5f), X(6), Y(16));
                    g.DrawLine(pen, X(18), Y(11.5f), X(18), Y(16));
                }
            }
        }
    }
}