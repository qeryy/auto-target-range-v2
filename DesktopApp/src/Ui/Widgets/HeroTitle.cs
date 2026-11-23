// 灵枢桌面版 —— 首页 hero 大标题
//
// 对齐 web 版 home.css 的 .lead / .lead-em：
//   列宽够时一行「下达任务，剩下的交给 灵枢」；
//   列宽不够才折成两行（第一行「下达任务，」）。
//   「灵枢」楷体、蓝→金渐变，其余黑体大字。
//
// 黑体部分用 TextRenderer（GDI）画：字距准确、深色底上清晰。
// 渐变字不能用 TextRenderer（只接受纯色），把字形转成 GraphicsPath 再用
// LinearGradientBrush 填充。em 尺寸按 Graphics 的 DPI 换算成像素，
// 垂直方向用 ascent 对齐基线，水平方向接在黑体实测宽度之后。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class HeroTitle : Control
    {
        private bool _oneLine;

        public HeroTitle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Height = PreferredHeight;
            BackColor = Theme.Bg;
        }

        // 矩形四角要露出父级氛围背景，不能铺纯色（否则字背后一块黑补丁）
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            AmbientPanel.PaintBase(this, e.Graphics);
        }

        /// <summary>两行字的实际像素高换算回设计单位（ScaleTree 会再乘 UiScale）。</summary>
        public static int PreferredHeight
        {
            get
            {
                int px = Theme.FontHero.Height + Theme.K(2)
                       + Math.Max(Theme.FontHero.Height, Theme.FontHeroEm.Height) + Theme.K(6);
                return (int)Math.Ceiling(px / Theme.UiScale);
            }
        }

        /// <summary>
        /// 按物理列宽决定一行还是两行，并把 Height 设成对应物理像素高。
        /// 由 HomePage.Layout 在缩放落定后调用；渐变字实测宽度留一点余量，
        /// 避免 GraphicsPath 字形比 GDI 量宽时贴边裁切。
        /// </summary>
        public void FitWidth(int width)
        {
            const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.NoPadding;
            int preW = TextRenderer.MeasureText("下达任务，剩下的交给", Theme.FontHero, Size.Empty, Flags).Width;
            int emW = TextRenderer.MeasureText("灵枢", Theme.FontHeroEm, Size.Empty, Flags).Width;
            _oneLine = preW + Theme.K(6) + emW <= width;
            int lineH = Math.Max(Theme.FontHero.Height, Theme.FontHeroEm.Height);
            Height = _oneLine
                ? lineH + Theme.K(6)
                : Theme.FontHero.Height + Theme.K(2) + lineH + Theme.K(6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.NoPadding;

            string pre = _oneLine ? "下达任务，剩下的交给" : "剩下的交给";
            int lineH = Math.Max(Theme.FontHero.Height, Theme.FontHeroEm.Height);
            int py = _oneLine
                ? (lineH - Theme.FontHero.Height) / 2
                : Theme.FontHero.Height + Theme.K(2);
            if (!_oneLine)
                TextRenderer.DrawText(g, "下达任务，", Theme.FontHero, new Point(0, 0), Theme.Text, Flags);
            TextRenderer.DrawText(g, pre, Theme.FontHero, new Point(0, py), Theme.Text, Flags);
            int preW = TextRenderer.MeasureText(g, pre, Theme.FontHero, Size.Empty, Flags).Width;

            // 像素字号的 Font.Size 本身就是像素，不再按 Graphics DPI 换算
            float emPx = Theme.FontHeroEm.Size;
            float ey = py + AscentPx(Theme.FontHero) - AscentPx(Theme.FontHeroEm);

            using (var sf = new StringFormat())
            using (var path = new GraphicsPath())
            {
                path.AddString("灵枢", Theme.FontHeroEm.FontFamily, (int)FontStyle.Bold,
                    emPx, new PointF(preW + Theme.K(2), ey), sf);
                var bounds = path.GetBounds();
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    // web 是 CSS 115deg（左→右偏下）；GDI 角度从 +x 轴顺时针量，换算约 25°
                    using (var gb = new LinearGradientBrush(bounds, Theme.AccentInk, Theme.Warn, 25f))
                    {
                        var blend = new ColorBlend(3);
                        blend.Colors = new[] { Theme.AccentInk, Theme.Accent, Theme.Warn };
                        blend.Positions = new[] { 0f, 0.52f, 1f };
                        gb.InterpolationColors = blend;
                        g.FillPath(gb, path);
                    }
                }
            }
        }

        private static float AscentPx(Font f)
        {
            var fam = f.FontFamily;
            int em = fam.GetEmHeight(FontStyle.Bold);
            if (em <= 0) return f.Size;
            return fam.GetCellAscent(FontStyle.Bold) / (float)em * f.Size;
        }
    }
}