// 灵枢桌面版 —— 圆角按钮
//
// WinForms 原生 Button 是直角。web 端的按钮是圆角（6–8px），这是两边观感
// 差别最大的一处。这里自绘一个圆角按钮：
//   * 主按钮：蓝色实心 + 稍亮的悬停色
//   * 次按钮：深色底 + 细描边
//   * 危险按钮：红色描边 + 红色字
//
// 键盘焦点用一圈更亮的描边表示，保留 Tab 导航（桌面软件该有的行为）。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public enum ButtonKind
    {
        Primary,
        Ghost,
        Danger,
    }

    public sealed class FlatButton : Button
    {
        private bool _hover;
        private bool _down;

        public ButtonKind Kind { get; set; }
        public int Radius { get; set; }

        public FlatButton()
        {
            Kind = ButtonKind.Ghost;
            // **胶囊**：半径取高度一半（SaltUI 的按钮就是这个形状）。
            // 原来固定 7，在 34px 高的按钮上看着还是「圆角方块」。
            Radius = Theme.RadiusPill;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontBody;
            ForeColor = Theme.Text;

            // 无障碍：自绘（UserPaint）会让 UIA/MSAA 把控件认成 Pane——
            // 屏幕阅读器读不出「这是按钮」，也没法代执行点击（122 轮实测）。
            // 显式声明角色能纠正类型识别；Name 用 Text（含图标的按钮读起来
            // 不会带着「◎」这种装饰字符，读屏软件按 role+name 播报「按钮 模型」）。
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _down = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        // 胶囊圆角外的四角露父级背景：氛围面板上采样同一幅光，普通页上铺 BackColor
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            AmbientPanel.PaintBase(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            bool enabled = Enabled;
            Color back, fore;

            // 圆角外的四角要在 OnPaint 里铺：Button 派生类的 OnPaintBackground
            // 实测不会被调用，那四个角就留着双缓冲回收前主人（别的同尺寸控件）的像素。
            AmbientPanel.PaintBase(this, g);

            switch (Kind)
            {
                case ButtonKind.Primary:
                    // 禁用/按下/悬停的底都按主题算 —— 写死 Color.FromArgb 的色值
                    // 在深/浅切换时会突兀（浅色下出现一块深蓝或亮蓝）。
                    if (!enabled) { back = Theme.Mix(Theme.Accent, Theme.Surface, 0.35); fore = Theme.Muted; }
                    else if (_down) { back = Theme.Mix(Theme.Accent, Theme.AccentHover, 0.5); fore = Color.White; }
                    else if (_hover) { back = Theme.AccentHover; fore = Color.White; }
                    else { back = Theme.Accent; fore = Color.White; }
                    break;

                case ButtonKind.Danger:
                    if (!enabled) { back = Theme.Surface; fore = Theme.Muted; }
                    else if (_down) { back = Theme.Mix(Theme.Danger, Theme.Surface, 0.26); fore = Theme.Danger; }
                    else if (_hover) { back = Theme.Mix(Theme.Danger, Theme.Surface, 0.18); fore = Theme.Danger; }
                    else { back = Theme.Mix(Theme.Danger, Theme.Surface, 0.10); fore = Theme.Danger; }
                    break;

                default: // Ghost：无描边的软底块，比所在面略深/略亮一档
                    if (!enabled) { back = Theme.Surface; fore = Theme.Muted; }
                    else if (_down) { back = Theme.Hover; fore = Theme.Text; }
                    else if (_hover) { back = Theme.Hover; fore = Theme.Text; }
                    else { back = Theme.Mix(Theme.Surface, Theme.Bg, 0.35); fore = Theme.Text; }
                    break;
            }

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            // 主按钮加一层很淡的投影，让它「浮」起来一点（SaltUI 的主按钮有 elevation）
            if (Kind == ButtonKind.Primary && enabled)
                Theme.DrawShadow(g, rect, Radius, Theme.K(3), 30);

            // 只铺填充：原来还沿轮廓描了一圈 border（颜色 == back），
            // 等于在抗锯齿边缘再叠一层同色像素，只会让边发虚，没有视觉收益。
            using (var path = Theme.RoundPath(rect, Radius))
            using (var b = new SolidBrush(back))
                g.FillPath(b, path);

            // 键盘焦点：外圈一圈浅色描边。
            // 半径必须夹到焦点矩形半高以内：胶囊按钮的 Radius 是「整高一半」，
            // 焦点矩形比按钮小 5px，半径不变就会超过半高、圆角路径自交，
            // 左右各画出一牙蓝色月牙——就是用户报的「边框蓝斑」。
            if (Focused && ShowFocusCues)
            {
                var focusRect = new Rectangle(Theme.K(2), Theme.K(2), Width - Theme.K(5), Height - Theme.K(5));
                int fr = Math.Min(Theme.K(Radius), focusRect.Height / 2);
                using (var p = new Pen(Color.FromArgb(140, Theme.Accent), Theme.Kf(2)))
                using (var path = Theme.RoundPath(focusRect, fr))
                {
                    g.DrawPath(p, path);
                }
            }

            TextRenderer.DrawText(g, Text, Font,
                ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>按钮工厂：与 Theme 里的 Primary/Ghost/Danger 并存，逐步替换。</summary>
    public static class FlatButtons
    {
        public static FlatButton Primary(string text, int width = 104, int height = 34)
        {
            return new FlatButton
            {
                Text = text,
                Kind = ButtonKind.Primary,
                Size = new Size(width, height),
            };
        }

        public static FlatButton Ghost(string text, int width = 88, int height = 34)
        {
            return new FlatButton
            {
                Text = text,
                Kind = ButtonKind.Ghost,
                Size = new Size(width, height),
            };
        }

        public static FlatButton Danger(string text, int width = 88, int height = 34)
        {
            return new FlatButton
            {
                Text = text,
                Kind = ButtonKind.Danger,
                Size = new Size(width, height),
            };
        }
    }
}
