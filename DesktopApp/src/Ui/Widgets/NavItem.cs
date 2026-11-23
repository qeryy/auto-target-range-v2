// 灵枢桌面版 —— 侧栏导航项（胶囊）
//
// ## 为什么不用原生 Button
//
// 原生 Button 即使 `FlatStyle.Flat` 也是**矩形**。侧栏占整个左侧 212px 宽、
// 13 项竖排，是全屏最容易看出「粗糙」的地方——一排方块挤在一起。
//
// SaltUI 的导航项是**胶囊**：选中时整条圆角高亮，未选中时完全透明、只变字色。
// 这里自绘一个同形状的。
//
// ## 三态
//
//   · 未选中 —— 透明底，`Muted` 字
//   · 悬停   —— `Hover` 底（很淡），`Text` 字
//   · 选中   —— `Accent` 底，白字，左侧一条竖直的高亮条
//
// ## 继承 Button 而不是 Control
//
// `MainForm._navButtons` 是 `Dictionary<string, Button>`，导航逻辑（`GoAsync`）
// 靠它改状态。继承 Button 就不用动那些代码，`NavItem` 只是换了画法。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class NavItem : Button
    {
        private bool _hover;

        /// <summary>是否当前页。</summary>
        public bool Active { get; private set; }

        /// <summary>左侧竖条的颜色（没选中的时候不画）。</summary>
        public Color BarColor { get; set; }

        public NavItem()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            TabStop = false;
            Font = Theme.FontBody;
            BackColor = Theme.Surface;
            ForeColor = Theme.Muted;
            BarColor = Theme.Accent;
            // 无障碍：自绘会让 UIA 认成 Pane，显式声明角色（同 FlatButton）
            AccessibleRole = AccessibleRole.PushButton;
        }

        /// <summary>切换选中态（`MainForm` 切页时调）。</summary>
        public void SetActive(bool active)
        {
            if (Active == active) return;
            Active = active;
            Invalidate();
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

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Button 派生类上这条路径实测不会被调用（铺底见 OnPaint），
            // 保留是为了万一走到非缓冲通路时，底色也是侧栏色而不是 Button 默认的浅灰。
            using (var b = new SolidBrush(Theme.Sidebar))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 铺底必须画在 OnPaint 里。`OnPaintBackground` 在 Button 派生类上
            // **根本不会被调用**（实测：改成亮红，屏幕上一点痕迹都没有），于是
            // 双缓冲的画布内容就是「上一次借用到这块缓冲的那个控件」的像素——
            // 侧栏所有导航项尺寸相同，缓冲区在它们和「+ 新任务」「v2.0.0」「配置」
            // 这些同尺寸控件之间反复回收，旧文字就被压印进来，形成残影。
            // 这里无条件先刷一层侧栏实底，残影就没有落脚的旧像素了。
            using (var b = new SolidBrush(Theme.Sidebar))
                g.FillRectangle(b, ClientRectangle);

            if (Width < Theme.K(12) || Height < Theme.K(8)) return;

            // 圆角矩形区域：左右各留 10px，上下留 3px（对齐 web .side nav button 的 8px 圆角）
            var pill = new Rectangle(Theme.K(10), Theme.K(3), Width - Theme.K(20), Height - Theme.K(6));
            if (pill.Width > Theme.K(4) && pill.Height > Theme.K(4))
            {
                // 选中 = 软高亮底（accent 15% 罩在侧栏渐变上）+ 亮蓝字，
                // 对齐 web `.side nav button.on { background: var(--accent-soft); color: var(--accent-ink) }`。
                // 悬停 = 比侧栏亮一档的软底，不画边框。
                Color fill = Active ? Theme.Mix(Theme.Accent, Theme.Sidebar, 0.15)
                           : _hover ? Theme.Mix(Theme.Surface, Theme.Sidebar, 0.5)
                           : Color.Empty;
                if (!fill.IsEmpty)
                {
                    using (var path = Theme.RoundPath(pill, Theme.K(10)))
                    using (var b = new SolidBrush(fill))
                        g.FillPath(b, path);
                }
            }

            Color fore = Active ? Theme.AccentInk
                       : _hover ? Theme.Text
                       : Theme.Muted;

            var textRect = new Rectangle(Theme.K(22), 0, Width - Theme.K(30), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}
