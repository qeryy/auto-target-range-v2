// 灵枢桌面版 —— 深色下拉选择器
//
// 原生 ComboBox 即使 OwnerDraw，右侧箭头按钮和边框仍由系统画成浅色，
// 在深色 composer 里是一块白斑。这里自绘：圆角深色底 + 文字 +  caret，
// 点击弹出深色 ContextMenuStrip 供选择。行为与下拉框一致（单选、可键盘 Esc 关）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class DarkSelect : Control
    {
        private bool _hover;
        private ContextMenuStrip _menu;

        public List<string> Items { get; private set; }
        public string SelectedItem { get; set; }
        public event EventHandler SelectedIndexChanged;

        public DarkSelect()
        {
            Items = new List<string>();
            SelectedItem = "";
            Height = 30;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Theme.Surface;
            AccessibleRole = AccessibleRole.ComboBox;
        }

        /// <summary>重建候选并尽量保留原选中项。</summary>
        public void SetItems(IEnumerable<string> items, string keep)
        {
            Items = new List<string>(items);
            if (Items.Count == 0) Items.Add("");
            SelectedItem = Items.Contains(keep) ? keep : Items[0];
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_menu != null) { _menu.Dispose(); _menu = null; }
            _menu = new ContextMenuStrip
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.FontSmall,
                Renderer = new DarkMenuRenderer(),
                ShowImageMargin = false,
            };
            foreach (var it in Items)
            {
                string val = it;
                var item = new ToolStripMenuItem(val);
                item.Click += delegate
                {
                    SelectedItem = val;
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                    Invalidate();
                };
                _menu.Items.Add(item);
            }
            // 不在 Closed 里同步 Dispose：ToolStrip 关闭流程后续还会访问自身，
            // 同步释放会抛 ObjectDisposedException（Escape 关菜单即崩）。
            // 旧菜单留到下次打开或控件释放时再回收。
            _menu.Show(this, new Point(0, Height));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(Theme.Surface))
                g.FillRectangle(bg, ClientRectangle);
            if (Width < Theme.K(20) || Height < Theme.K(10)) return;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundPath(rect, Theme.K(Theme.RadiusSm)))
            using (var b = new SolidBrush(Theme.Surface))
                g.FillPath(b, path);
            using (var pen = new Pen(_hover ? Theme.Line : Theme.LineDark))
            using (var path = Theme.RoundPath(rect, Theme.K(Theme.RadiusSm)))
                g.DrawPath(pen, path);

            var textRect = new Rectangle(Theme.K(10), 0, Width - Theme.K(34), Height);
            TextRenderer.DrawText(g, SelectedItem ?? "", Theme.FontSmall, textRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            // caret
            int cx = Width - Theme.K(18), cy = Height / 2;
            using (var b = new SolidBrush(Theme.Muted))
            {
                var pts = new[] { new Point(cx - Theme.K(4), cy - Theme.K(2)), new Point(cx + Theme.K(4), cy - Theme.K(2)), new Point(cx, cy + Theme.K(3)) };
                g.FillPolygon(b, pts);
            }
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColors()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Theme.Text;
                base.OnRenderItemText(e);
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuBorder { get { return Theme.LineDark; } }
            public override Color MenuItemBorder { get { return Theme.Hover; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
            public override Color MenuStripGradientBegin { get { return Theme.Surface; } }
            public override Color MenuStripGradientEnd { get { return Theme.Surface; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        }
    }
}