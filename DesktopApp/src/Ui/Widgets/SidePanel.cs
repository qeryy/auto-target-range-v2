// 灵枢桌面版 —— 侧栏底（渐变软面）
//
// 对齐 web `.side` 的背景：
//   linear-gradient(165deg, color-mix(accent 3%, surface), surface 42%)
// 顶端掺一点主色、往下回到侧栏色——侧栏不再是「一块灰板」，
// 而是和内容区同源、但被光扫过的一面。
//
// 画在 OnPaintBackground：侧栏里的透明子控件（组标题/空态字）和自绘子控件
// （NavItem/SessionItem/BrandPanel，经 PaintBase）都采样这一层，才不会露补丁。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class SidePanel : Panel
    {
        public SidePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Theme.Sidebar;
        }

        /// <summary>自绘子控件铺底：向上找侧栏面，采样同一幅渐变。</summary>
        public static void PaintBase(Control c, Graphics g)
        {
            int ox = 0, oy = 0;
            for (Control p = c; p != null; p = p.Parent)
            {
                var side = p as SidePanel;
                if (side != null)
                {
                    var st = g.Save();
                    g.TranslateTransform(-ox, -oy);
                    side.PaintSide(g);
                    g.Restore(st);
                    return;
                }
                ox += p.Left;
                oy += p.Top;
            }
            using (var b = new SolidBrush(Theme.Sidebar))
                g.FillRectangle(b, c.ClientRectangle);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintSide(e.Graphics);
        }

        private void PaintSide(Graphics g)
        {
            int w = Math.Max(Width, 1), h = Math.Max(Height, 1);
            // 整条侧栏统一实底：顶端那层 5% 主色光改成了「面板实底 + 子控件实底」，
            // 因为透明采样路径在页面切换/窗口激活时会留下旧文字残影（实测），
            // 实底重绘没有可错位的旧像素，代价是丢掉那层很淡的光。
            using (var b = new SolidBrush(Theme.Sidebar))
                g.FillRectangle(b, 0, 0, w, h);

            // 与内容区之间一根暗线
            using (var pen = new Pen(Theme.LineDark))
                g.DrawLine(pen, w - 1, 0, w - 1, h);
        }
    }
}