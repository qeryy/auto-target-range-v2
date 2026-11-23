// 灵枢桌面版 —— 迷你折线图
//
// ## 用途
//
// 仪表盘的「近 14 天活跃」趋势。web 版这块是 SVG 折线
// （`dashboard.css` 里有 `.dash-trend-svg text { fill: var(--ink-3); font: 10px var(--mono); }`），
// 桌面版用 GDI+ 画同样的东西。
//
// ## 为什么自己写而不是用 Chart 控件
//
// `System.Windows.Forms.DataVisualization.Charting` 要额外引程序集，而且默认
// 主题是白底灰网格 —— 要在深色 UI 里用，得把它所有颜色重设一遍，
// 代码量比直接画还多。这个折线图只需要几十行：一条折线、一层渐变、几个点、四个刻度。
//
// ## 画法
//
//   1. 底部一条基线（LineDark）
//   2. 折线下面的面积：从 Accent 40% 渐变到全透明
//   3. 折线本身：Accent，2px，抗锯齿
//   4. 每个数据点画一个小圆（半径 2.5），最后一个高亮
//   5. 左下角标起始日期、右下角标今天；左上角标峰值
//
// 数据为空或全是 0 时画一条水平的 "还没有数据" 提示，不画空折线。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class Sparkline : Control
    {
        private double[] _values = new double[0];
        private string[] _labels = new string[0];
        private string _emptyText = "还没有数据";

        public Sparkline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.FontTiny;
        }

        /// <summary>空数据时显示的提示。</summary>
        public string EmptyText
        {
            get { return _emptyText; }
            set { _emptyText = value ?? ""; Invalidate(); }
        }

        /// <summary>喂数据。`labels` 可空，只用于两端标注。</summary>
        public void SetData(double[] values, string[] labels)
        {
            _values = values ?? new double[0];
            _labels = labels ?? new string[0];
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 采样所在卡面（含色晕），折线图像「长在卡上」而不是贴一块深色矩形
            AmbientPanel.PaintBase(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var area = new Rectangle(0, 0, Width, Height);
            if (area.Width < Theme.K(30) || area.Height < Theme.K(20)) return;

            // 上下留白：顶部给峰值标注，底部给基线
            int padTop = Theme.K(18);
            int padBottom = Theme.K(18);
            var plot = new Rectangle(Theme.K(2), padTop, area.Width - Theme.K(4), area.Height - padTop - padBottom);
            if (plot.Height < Theme.K(12)) return;

            // ---- 基线 ----
            using (var pen = new Pen(Theme.LineDark, Theme.Kf(1)))
                g.DrawLine(pen, plot.X, plot.Bottom, plot.Right, plot.Bottom);

            // ---- 空数据 ----
            double max = 0;
            for (int i = 0; i < _values.Length; i++) if (_values[i] > max) max = _values[i];
            if (_values.Length == 0 || max <= 0)
            {
                TextRenderer.DrawText(g, _emptyText, Font, plot, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix);
                return;
            }

            int n = _values.Length;
            // 只有一个点时居中画，避免除以零
            Func<int, float> xAt = delegate(int i)
            {
                if (n <= 1) return plot.X + plot.Width / 2f;
                return plot.X + (float)plot.Width * i / (n - 1);
            };
            Func<double, float> yAt = delegate(double v)
            {
                double t = v / max;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                return plot.Bottom - (float)(plot.Height * t);
            };

            var pts = new PointF[n];
            for (int i = 0; i < n; i++) pts[i] = new PointF(xAt(i), yAt(_values[i]));

            // ---- 面积（折线下方渐变）----
            if (n >= 2)
            {
                var poly = new PointF[n + 2];
                for (int i = 0; i < n; i++) poly[i] = pts[i];
                poly[n] = new PointF(pts[n - 1].X, plot.Bottom);
                poly[n + 1] = new PointF(pts[0].X, plot.Bottom);

                using (var grad = new LinearGradientBrush(
                    new Rectangle(plot.X, plot.Y, plot.Width, Math.Max(Theme.K(2), plot.Height)),
                    Color.FromArgb(64, Theme.Accent),
                    Color.FromArgb(0, Theme.Accent),
                    LinearGradientMode.Vertical))
                {
                    g.FillPolygon(grad, poly);
                }
            }

            // ---- 折线 ----
            using (var pen = new Pen(Theme.Accent, Theme.Kf(2)))
            {
                pen.LineJoin = LineJoin.Round;
                if (n >= 2) g.DrawLines(pen, pts);
            }

            // ---- 数据点 ----
            for (int i = 0; i < n; i++)
            {
                bool last = (i == n - 1);
                int r = Theme.K(last ? 3 : 2);
                using (var b = new SolidBrush(last ? Theme.Accent : Theme.Mix(Theme.Accent, Theme.Surface, 0.55)))
                    g.FillEllipse(b, pts[i].X - r, pts[i].Y - r, r * 2, r * 2);
            }

            // ---- 峰值标注（左上）----
            string peak = "峰值 " + max.ToString("N0");
            TextRenderer.DrawText(g, peak, Font,
                new Rectangle(plot.X, 0, plot.Width, padTop), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // ---- 两端日期 ----
            if (_labels.Length > 0)
            {
                string left = _labels[0];
                string right = _labels[_labels.Length - 1];
                TextRenderer.DrawText(g, left, Font,
                    new Rectangle(plot.X, plot.Bottom + Theme.K(1), plot.Width / 2, padBottom - Theme.K(1)), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, right, Font,
                    new Rectangle(plot.X + plot.Width / 2, plot.Bottom + Theme.K(1), plot.Width / 2, padBottom - Theme.K(1)),
                    Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
