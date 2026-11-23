// 灵枢桌面版 —— 侧栏品牌区
//
// 对齐 web 版 `static/index.html` 的 `.brand`：
//   · 节点图标（web 是 SVG brand-mark：中心圆 + 三向连线 + 三个端点圆）
//   · 「灵枢·SpiritScout」，间隔符「·」用 accent 色
//   · 副标题「渗透测试智能体集群」，等宽小字
//   · 背景是 165° 渐变：顶端掺 3% accent，42% 处回到 Surface
//
// 实现上整个区域**全部在 OnPaint 里画**，不放子 Label：
// WinForms 的 Label 设 BackColor=Transparent 时抓的是父控件 OnPaintBackground
// 那一层，父控件自绘渐变它抓不到，会出现文字底下一块纯色补丁。
// 全自绘反而没有这个兼容性问题，字号/基线还能精确控制。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class BrandPanel : Panel
    {
        // Font 必须是静态缓存的。**不能每帧在 OnPaint 里 new Font**——
        // Font 持有 GDI 字体句柄，每个进程默认上限 10,000 个 GDI 对象，
        // 高频重绘（比如切页面、拖动窗口）几分钟就把句柄池耗光，
        // 然后所有绘图一起崩掉（"Parameter is not valid"）。
        // 这是 WinForms 自绘最经典的坑，和「有没有 Dispose」关系不大——
        // 就算 Dispose 了，反复创建销毁也是浪费。
        // 像素字号（Theme.F 按 UiScale 换算），静态初始化发生在 InitScale 之后。
        private static readonly Font TitleFont = Theme.F(13F, FontStyle.Bold);
        private static readonly Font TitleFont12 = Theme.F(12F, FontStyle.Bold);
        private static readonly Font TitleFont11 = Theme.F(11F, FontStyle.Bold);
        private static readonly Font TitleFont10 = Theme.F(10F, FontStyle.Bold);
        private static readonly Font SubtitleBold = Theme.F(9.5F, FontStyle.Bold);
        // 自适应候选（从大到小）。不在 OnPaint 里 new Font（GDI 句柄坑）。
        private static readonly Font[] FitFonts = { TitleFont, TitleFont12, TitleFont11, TitleFont10 };

        public BrandPanel()
        {
            Height = 74;
            Dock = DockStyle.Top;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // ---- 底：实底侧栏色（采样渐变会在切页/激活时留下旧像素残影）----
            using (var sideB = new SolidBrush(Theme.Sidebar))
                g.FillRectangle(sideB, ClientRectangle);

            // ---- logo 块：蓝→紫渐变圆角方块 + 白色节点图标 ----
            var tile = new Rectangle(Theme.K(16), Theme.K(15), Theme.K(36), Theme.K(36));
            using (var path = Theme.RoundPath(tile, Theme.K(10)))
            using (var gb = new LinearGradientBrush(tile, Theme.Accent, Theme.Violet, 135f))
                g.FillPath(gb, path);
            DrawMark(g, new Rectangle(tile.X + Theme.K(7), tile.Y + Theme.K(7), Theme.K(22), Theme.K(22)), Color.White);

            int tx = tile.Right + Theme.K(12);

            // ---- 标题：灵枢 · SpiritScout（按宽度自适应，保证不截断）----
            string a = "灵枢";
            string dot = "·";
            string b = "SpiritScout";
            int avail = Math.Max(Theme.K(40), Width - tx - Theme.K(10));
            int ty = Theme.K(17);

            Size sa = TextRenderer.MeasureText(g, a, TitleFont, Size.Empty, TextFormatFlags.NoPadding);
            Size sd = TextRenderer.MeasureText(g, dot, TitleFont, Size.Empty, TextFormatFlags.NoPadding);
            Size sb = TextRenderer.MeasureText(g, b, SubtitleBold, Size.Empty, TextFormatFlags.NoPadding);

            if (sa.Width + sd.Width + sb.Width <= avail)
            {
                // 首选：大「灵枢」+ 小英文名（对齐 web 的 h1 + em）
                TextRenderer.DrawText(g, a, TitleFont, new Point(tx, ty), Theme.Text, TextFormatFlags.NoPadding);
                tx += sa.Width;
                TextRenderer.DrawText(g, dot, TitleFont, new Point(tx, ty), Theme.Accent, TextFormatFlags.NoPadding);
                tx += sd.Width;
                TextRenderer.DrawText(g, b, SubtitleBold, new Point(tx, ty + Theme.K(3)), Theme.AccentInk, TextFormatFlags.NoPadding);
            }
            else
            {
                // 放不下：整体降到单一字号，挑第一个能装下的，杜绝截断
                Font fit = FitFonts[FitFonts.Length - 1];
                foreach (var f in FitFonts)
                {
                    int w = TextRenderer.MeasureText(g, a + dot + b, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                    if (w <= avail) { fit = f; break; }
                }
                Size fa = TextRenderer.MeasureText(g, a, fit, Size.Empty, TextFormatFlags.NoPadding);
                Size fd = TextRenderer.MeasureText(g, dot, fit, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, a, fit, new Point(tx, ty), Theme.Text, TextFormatFlags.NoPadding);
                tx += fa.Width;
                TextRenderer.DrawText(g, dot, fit, new Point(tx, ty), Theme.Accent, TextFormatFlags.NoPadding);
                tx += fd.Width;
                TextRenderer.DrawText(g, b, fit, new Point(tx, ty), Theme.AccentInk, TextFormatFlags.NoPadding);
            }

            // ---- 副标题（等宽、略字距，对齐 web 的 .brand small）----
            TextRenderer.DrawText(g, "渗透测试智能体集群", Theme.FontTiny,
                new Point(tile.Right + Theme.K(12), Theme.K(44)), Theme.Muted, TextFormatFlags.NoPadding);
        }

        /// <summary>按 web 版 SVG 的路径画节点图标（viewBox 24x24 等比缩放）。</summary>
        private static void DrawMark(Graphics g, Rectangle box, Color color)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                float k = box.Width / 24f;
                Func<float, float> X = v => box.Left + v * k;
                Func<float, float> Y = v => box.Top + v * k;

                using (var pen = new Pen(color, Math.Max(Theme.Kf(1.6f), 2f * k)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, X(12), Y(9.4f), X(12), Y(4.8f));
                    g.DrawLine(pen, X(14.3f), Y(13.2f), X(18.1f), Y(15.4f));
                    g.DrawLine(pen, X(9.7f), Y(13.2f), X(5.9f), Y(15.4f));
                }

                using (var br = new SolidBrush(color))
                {
                    float rc = 2.7f * k;
                    g.FillEllipse(br, X(12) - rc, Y(12) - rc, rc * 2, rc * 2);
                    float ro = 1.8f * k;
                    g.FillEllipse(br, X(12) - ro, Y(3.7f) - ro, ro * 2, ro * 2);
                    g.FillEllipse(br, X(18.5f) - ro, Y(17.4f) - ro, ro * 2, ro * 2);
                    g.FillEllipse(br, X(5.5f) - ro, Y(17.4f) - ro, ro * 2, ro * 2);
                }
            }
            finally { g.SmoothingMode = old; }
        }
    }
}
