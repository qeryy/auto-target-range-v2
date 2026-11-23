// 灵枢桌面版 —— 深色标签容器
//
// 系统 TabControl 的标签条和页框是浅色主题色，压在深色会话页上是一圈白边。
// 这里用 OwnerDraw + Buttons 外观自绘标签（选中页底部一条亮蓝 underline，
// 对齐 web 的 tab 样式），并在 WM_PAINT 之后用背景色盖掉系统页框。

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class DarkTabs : TabControl
    {
        private const int WM_PAINT = 0x000F;
        private const int WM_PRINT = 0x0317;
        private const int WM_PRINTCLIENT = 0x0318;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        public DarkTabs()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            Appearance = TabAppearance.Buttons;
            SizeMode = TabSizeMode.Fixed;
            // ItemSize 不被 Control.Scale 缩放，直接按 UiScale 给物理值
            ItemSize = new Size(Theme.K(86), Theme.K(34));
            BackColor = Theme.Bg;
            Font = Theme.FontSmall;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT)
            {
                IntPtr dc = GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                try
                {
                    using (var g = Graphics.FromHdc(dc)) RepaintDark(g);
                }
                finally { ReleaseDC(Handle, dc); }
            }
            // DrawToBitmap / 截图走 WM_PRINT(CLIENT)，DC 在 wParam 里，同样重铺。
            else if (m.Msg == WM_PRINT || m.Msg == WM_PRINTCLIENT)
            {
                using (var g = Graphics.FromHdc(m.WParam)) RepaintDark(g);
            }
        }

        /// <summary>
        /// 系统只把「标签格」交给 DrawItem，标签条剩余背景和页区外框仍画成浅色。
        /// 这里在 base 画完后整条重铺：背景铺 Bg、逐格自绘标签、再沿页区外沿盖掉系统框。
        /// </summary>
        private void RepaintDark(Graphics g)
        {
            int stripH = DisplayRectangle.Top;
            using (var bg = new SolidBrush(Theme.Bg))
            {
                g.FillRectangle(bg, 0, 0, Width, stripH);
                for (int i = 0; i < TabPages.Count; i++)
                {
                    Rectangle r = GetTabRect(i);
                    bool sel = i == SelectedIndex;
                    using (var b = new SolidBrush(sel ? Theme.Surface : Theme.Bg))
                        g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, TabPages[i].Text, Font, r,
                        sel ? Theme.Text : Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    if (sel)
                        using (var p = new Pen(Theme.Accent, Theme.Kf(2)))
                            g.DrawLine(p, r.Left + Theme.K(12), r.Bottom - Theme.K(2), r.Right - Theme.K(12), r.Bottom - Theme.K(2));
                }
                // 页区外框 + 控件四边残留的浅色描边
                Rectangle d = DisplayRectangle;
                int edge = Theme.K(2);
                using (var pen = new Pen(Theme.Bg, Theme.Kf(4)))
                    g.DrawRectangle(pen, d.Left - 1, d.Top - 1, d.Width + 1, d.Height + 1);
                g.FillRectangle(bg, 0, 0, edge, Height);
                g.FillRectangle(bg, Width - edge, 0, edge, Height);
                g.FillRectangle(bg, 0, Height - edge, Width, edge);
            }
        }
    }
}