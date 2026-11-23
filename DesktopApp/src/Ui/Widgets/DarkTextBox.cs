// 灵枢桌面版 —— 深色边框输入框
//
// 原生 TextBox 的 `FixedSingle` 边框是**系统浅灰**，压在深色主题上是一条
// 亮线，和搜索框「空着一个白边盒子」的观感一起显得突兀。这里继承 TextBox
// （类型不变，27 个调用点不用动），在 WM_NCPAINT 之后用深色把那条系统边框
// 盖掉，得到与卡片描边同色的暗边框。
//
// 顺带提供 Placeholder（cue banner）：空框时显示灰色提示文字，
// 搜索框不再「空得不知道能干嘛」。

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class DarkTextBox : TextBox
    {
        private const int WM_NCPAINT = 0x0085;
        private const int WM_PAINT = 0x000F;
        private const int WM_PRINTCLIENT = 0x0318;
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private string _placeholder = "";

        public DarkTextBox()
        {
            BorderStyle = BorderStyle.FixedSingle;
        }

        /// <summary>空框时的灰色提示文字（cue banner，系统绘制、不占 Text）。</summary>
        public string Placeholder
        {
            get { return _placeholder; }
            set
            {
                _placeholder = value ?? "";
                if (IsHandleCreated) ApplyPlaceholder();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyPlaceholder();
        }

        private void ApplyPlaceholder()
        {
            if (_placeholder.Length == 0) return;
            // wParam=1：失去焦点也显示提示（搜索框常驻提示更友好）
            SendMessage(Handle, EM_SETCUEBANNER, (IntPtr)1, _placeholder);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCPAINT && BorderStyle == BorderStyle.FixedSingle)
                PaintDarkBorder();
            // 多行 edit 的系统 cue banner 不显示，自己在客户区画占位文字。
            // 放在 base 之后：edit 先画完底和光标，我们再补灰字，空框即有提示。
            else if (m.Msg == WM_PAINT && Multiline && _placeholder.Length > 0 && Text.Length == 0)
            {
                using (var g = CreateGraphics()) PaintPlaceholder(g);
            }
            // DrawToBitmap / 打印走 WM_PRINTCLIENT（wParam 是目标 DC），同样补占位字。
            else if (m.Msg == WM_PRINTCLIENT && Multiline && _placeholder.Length > 0 && Text.Length == 0)
            {
                using (var g = Graphics.FromHdc(m.WParam)) PaintPlaceholder(g);
            }
        }

        private void PaintPlaceholder(Graphics g)
        {
            TextRenderer.DrawText(g, _placeholder, Font, new Point(Theme.K(4), Theme.K(4)), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        /// <summary>用卡片描边色盖掉系统浅灰边框（1px，方形以完全覆盖）。</summary>
        private void PaintDarkBorder()
        {
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using (var g = Graphics.FromHdc(dc))
                using (var pen = new Pen(Theme.LineDark))
                {
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                }
            }
            finally { ReleaseDC(Handle, dc); }
        }
    }
}