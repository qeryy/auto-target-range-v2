// 灵枢桌面版 —— 开关（胶囊）
//
// ## 为什么不用系统 CheckBox
//
// WinForms 的 `CheckBox` 是**系统主题绘制**的：深色背景上它依然画一个
// 白底小方块 + 黑色勾，在一堆 #202020 / #1C1C1E 的界面里像贴了张白纸。
//
// web 版对应的是 `input[type=checkbox]` 自绘（`polish.css` 里 `::before`
// 画一个 `scale(0)→scale(1)` 的勾，带 transition）。
//
// ## 形状选择
//
// 这里做成**胶囊开关**（开关拨动那种）而不是方框加勾：
//
//   · 12 处用途里有 10 处是「启用/停用」这种二元状态，开关更直观
//   · 剩下 2 处（MCP/模型的「已启用」）同样适合开关
//   · 胶囊形状和按钮/导航/状态标签已经是同一套视觉语言
//
// ## 继承 CheckBox
//
// 为了**不改调用方**：所有地方都是 `new CheckBox { Text = ..., Checked = ... }`
// 然后读 `cb.Checked`。继承 `CheckBox` 之后 `Checked` / `Text` / `Click` 全部照旧，
// 只是换了画法。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class Switch : CheckBox
    {
        private bool _hover;

        /// <summary>胶囊宽度。高度由控件高度决定（扣掉上下 2px 留白）。</summary>
        public int TrackWidth { get; set; }

        public Switch()
        {
            TrackWidth = 38;
            // 默认高度 22：胶囊高 18 + 上下各 2px 留白
            Height = 22;
            Width = TrackWidth + 8 + 60;   // 轨道 + 间距 + 文字大概位置
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontBody;
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // ButtonBase 派生类上这条路径实测不会被调用（铺底见 OnPaint），
            // 保留是为了万一走到非缓冲通路时采的是所在软面而不是控件默认底色。
            AmbientPanel.PaintBase(this, e.Graphics);
        }

        /// <summary>
        /// 覆盖首选尺寸，让调用方的 `AutoSize = true` 算出**正确的**宽度。
        ///
        /// CheckBox 默认的 AutoSize 只量「文字 + 系统勾选框」，不知道我们自己
        /// 画了 38px 的轨道。不覆盖的话，换成 Switch 后控件会被压窄，
        /// 轨道和文字会重叠。
        /// </summary>
        public override Size GetPreferredSize(Size proposedSize)
        {
            var textSize = string.IsNullOrEmpty(Text)
                ? Size.Empty
                : TextRenderer.MeasureText(Text, Font);
            int w = Theme.K(TrackWidth) + Theme.K(8) + textSize.Width + Theme.K(4);
            int h = Math.Max(Theme.K(22), textSize.Height + Theme.K(4));
            return new Size(w, h);
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
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            Invalidate();
            base.OnCheckedChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 铺底必须在 OnPaint 里做：`Switch` 继承 `CheckBox`（→ ButtonBase），
            // 这类控件的 `OnPaintBackground` 实测不会被调用，双缓冲画布留着
            // 上一位借用者的文字——通知页四个开关因此互相压字。
            AmbientPanel.PaintBase(this, g);

            bool on = Checked;
            bool enabled = Enabled;

            int h = Math.Max(Theme.K(12), Height - Theme.K(4));
            int w = Math.Max(h + Theme.K(8), Theme.K(TrackWidth));
            int top = (Height - h) / 2;
            var track = new Rectangle(0, top, w, h);

            Color trackColor;
            if (!enabled) trackColor = Theme.LineDark;
            else if (on) trackColor = _hover ? Theme.AccentHover : Theme.Accent;
            else trackColor = _hover ? Theme.Line : Theme.LineDark;

            using (var path = Theme.RoundPath(track, Theme.RadiusPill))
            using (var b = new SolidBrush(trackColor))
                g.FillPath(b, path);

            // 圆点：比轨道小 6px，留 3px 内边距
            int knob = h - Theme.K(6);
            int knobX = on ? track.Right - knob - Theme.K(3) : track.X + Theme.K(3);
            int knobY = track.Y + Theme.K(3);
            Color knobColor = enabled ? Color.White : Theme.Muted;
            using (var b = new SolidBrush(knobColor))
                g.FillEllipse(b, knobX, knobY, knob, knob);

            // 文字（在轨道右边）
            if (!string.IsNullOrEmpty(Text))
            {
                int tx = w + Theme.K(8);
                int tw = Width - tx - Theme.K(2);
                if (tw > Theme.K(4))
                {
                    Color fore = enabled ? ForeColor : Theme.Muted;
                    TextRenderer.DrawText(g, Text, Font,
                        new Rectangle(tx, 0, tw, Height), fore,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
            }
        }
    }
}
