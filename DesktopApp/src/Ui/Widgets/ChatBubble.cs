// 灵枢桌面版 —— 会话气泡
//
// ## 目标：对齐 web 版的 .tl-user / .tl-conv 气泡
//
// web 版（`views.css` L413-429）的定义：
//
//   .tl-user .bubble {
//     background: color-mix(in srgb, var(--accent) 11%, var(--surface));
//     border: 1px solid color-mix(in srgb, var(--accent) 30%, var(--line));
//     border-radius: 13px 13px 4px 13px;
//   }
//   .tl-conv .bubble {
//     background: color-mix(in srgb, var(--violet) 9%, var(--surface));
//     border: 1px solid color-mix(in srgb, var(--violet) 30%, var(--line));
//     border-radius: 4px 13px 13px 13px;
//   }
//   /* 指向角：9×9 方块旋转 45°，压在气泡边上 */
//
// 三个关键点：
//
//   1. **底色是一层淡色雾，不是实心色**。用户气泡是「11% 的蓝混在卡片色上」，
//      文字仍是正常墨色；做成纯蓝底白字会显得饱和度过高、看着旧。
//   2. **四角半径不一样**。说话人那一侧的角收到 4px，其余 13px——
//      这是聊天气泡和普通卡片的区别所在。
//   3. **有个指向角**。小三角从气泡边上探出去，指回说话人。
//
// ## 内容按 Markdown 分块渲染
//
// 模型的回复基本是 Markdown。原来整段原文画出来，用户看到的是三个反引号和
// 一堆星号；现在按块渲染：标题加粗、列表带符号、代码块换等宽字体加深底。
//
// 解析在 `MarkdownLite`，这里只管画。
//
// ## 布局：为指向角和徽章留位
//
//   用户：  [        气泡(右下角小)  ]◤      ← 右边 TailW 给指向角
//   助手： [灵]  ◤[   气泡(左上角小)      ]  ← 左边 SealW+SealGap 给徽章

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public enum BubbleRole
    {
        User,
        Assistant,
        System,
    }

    public sealed class ChatBubble : Panel
    {
        /// <summary>指向角占用的宽度。</summary>
        private const int TailW = 6;

        /// <summary>
        /// 「灵枢」标识徽章的边长（web 版 .seal 是 25×25）。
        /// 全局字号加两个字号后同步放大，保持「徽章 ≈ 正文两倍见方」的比例。
        /// </summary>
        private const int SealW = 32;

        /// <summary>徽章与气泡之间的间距（web 版 flex 的 gap: 8px）。</summary>
        private const int SealGap = 8;

        /// <summary>
        /// 单块显示上限（字符）。超出就截断显示 + 提示。
        ///
        /// **不截会出事**：模型返回一段几千行的代码是常事，而气泡高度 =
        /// 所有块高度之和。`TextRenderer.MeasureText` 对 5000 行代码会算出
        /// 十万像素量级的高度，接着 `OnPaint` 要按这个高度画 —— 测量慢、绘制慢，
        /// 滚动时每个 tick 都在重绘一块十万像素的控件。
        ///
        /// 完整内容仍在 `_text` 里（右键「复制这条消息」能拿到全文），
        /// 只是**屏幕上**只画前面这一段。
        /// </summary>
        private const int MaxBlockChars = 8000;

        /// <summary>气泡内边距（对齐 web 的 `padding: 9px 14px`）。</summary>
        private const int PadX = 14;
        private const int PadY = 9;

        /// <summary>
        /// 徽章里「灵」字用的字体，**静态缓存**。
        ///
        /// 原来是在 `DrawSeal` 里 `using (var f = new Font(...))` —— 虽然 `using`
        /// 会释放，但那是**每次重绘都新建一个 GDI+ 字体对象**。
        /// 会话页默认渲染 150 个气泡、其中助手的有几十个，一次重绘就是几十次
        /// 字体创建/销毁。`Font` 在 GDI+ 里不便宜，而字号是固定的，没理由每次都建。
        /// </summary>
        private static readonly Font SealFont = Theme.F(10F);

        /// <summary>块之间的垂直间距。</summary>
        private const int BlockGap = 6;

        /// <summary>代码块的内边距。</summary>
        private const int CodePadX = 10;
        private const int CodePadY = 8;

        private readonly string _text;
        private readonly BubbleRole _role;
        /// <summary>总块数上限。一条消息几百个股息块也没意义，超出只画前面这些。</summary>
        private const int MaxBlocks = 200;

        /// <summary>
        /// 这一块**实际画出去**的文本（超长截断 + 提示）。
        ///
        /// 测量和绘制必须用同一个字符串 —— 否则量出来 5000 行、画出来 200 行，
        /// 气泡底下会留一大片空白。
        /// </summary>
        private static string DisplayText(MdBlock b)
        {
            string t = b == null ? "" : (b.Text ?? "");
            if (t.Length <= MaxBlockChars) return t;
            return t.Substring(0, MaxBlockChars)
                + "\n…（这一块共 " + t.Length.ToString("N0")
                + " 字符，只显示前 " + MaxBlockChars + " 个；右键「复制这条消息」拿完整内容）";
        }

        private readonly List<MdBlock> _blocks;

        /// <summary>
        /// 每块的尺寸，**构造时算一次，之后只读**。
        ///
        /// 原来 `PaintBlocks` 里每画一块就 `MeasureBlock` 一次——但块内容在
        /// 生命周期内不变，尺寸早就在构造时算过了。这等于每次重绘都把
        /// 「150 个气泡 × 13 块」的 MeasureText 全部重算一遍。
        /// 缓存之后重绘只做绘制，不测量。
        /// </summary>
        private readonly Size[] _blockSizes;

        private bool _hover;

        public ChatBubble(BubbleRole role, string text, int maxWidth)
        {
            _role = role;
            _text = text ?? "";
            _blocks = MarkdownLite.Parse(_text);

            // 块数上限：几百块的单条消息既没必要画，也会把控件撑得很高
            if (_blocks.Count > MaxBlocks)
            {
                int dropped = _blocks.Count - MaxBlocks;
                _blocks = _blocks.GetRange(0, MaxBlocks);
                var tail = new MdBlock { Kind = MdKind.Paragraph };
                tail.Text = "…（本条消息还有 " + dropped + " 个段落未显示）";
                _blocks.Add(tail);
            }
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            Padding = new Padding(0);

            bool system = role == BubbleRole.System;
            // 气泡在运行时按物理宽度构造（消息流布局落定后才 LoadHistory），
            // 所有设计常量在这里直接 K() 成物理值，不再经过 ScaleTree。
            int padX = Theme.K(system ? 6 : PadX);
            int padY = Theme.K(system ? 6 : PadY);
            int padRight = Theme.K(system ? 0 : TailW);
            int padLeft = Theme.K(system ? 0 : (role == BubbleRole.Assistant ? SealW + SealGap : 0));

            int contentMax = Math.Max(Theme.K(120), maxWidth - padX * 2 - padRight - padLeft);

            // 量出每块的尺寸与总高（**存下来，重绘时不再测量**）
            _blockSizes = new Size[_blocks.Count];
            int totalH = 0;
            int widest = 0;
            for (int i = 0; i < _blocks.Count; i++)
            {
                var sz = MeasureBlock(_blocks[i], contentMax, system);
                _blockSizes[i] = sz;
                if (i > 0) totalH += Theme.K(BlockGap);
                totalH += sz.Height;
                if (sz.Width > widest) widest = sz.Width;
            }
            if (totalH <= 0) totalH = Theme.K(system ? 20 : 24);

            int w = Math.Min(maxWidth, widest + padX * 2 + padRight + padLeft + Theme.K(4));
            int h = totalH + padY * 2;
            int minH = Theme.K(system ? 26 : (role == BubbleRole.Assistant ? 35 : 36));
            Size = new Size(w, Math.Max(h, minH));

            // 气泡内容没法用鼠标选中——模型给出的代码和命令用户一个字符都拿不到。
            // 右键菜单把整条消息原文送进剪贴板，是最省事也最够用的补法。
            var menu = new ContextMenuStrip();
            menu.Items.Add("复制这条消息", null, (s2, e2) =>
            {
                try { Clipboard.SetText(_text); } catch { }
            });
            menu.Items.Add("复制为代码块", null, (s2, e2) =>
            {
                try { Clipboard.SetText("```\r\n" + _text + "\r\n```"); } catch { }
            });
            ContextMenuStrip = menu;

            Margin = role == BubbleRole.User
                ? new Padding(0, Theme.K(4), Theme.K(4), Theme.K(4))
                : new Padding(Theme.K(4), Theme.K(4), 0, Theme.K(4));
        }

        // ===================== 测量 =====================

        private static Font FontFor(MdBlock b, bool system)
        {
            if (system) return Theme.FontSmall;
            switch (b.Kind)
            {
                case MdKind.Heading:
                    if (b.Level <= 1) return Theme.FontHeading;
                    return Theme.FontBody;          // 二三级标题靠加粗区分
                case MdKind.Code:
                    return Theme.FontMono;
                default:
                    return Theme.FontBody;
            }
        }

        private static Size MeasureBlock(MdBlock b, int maxW, bool system)
        {
            if (b.Kind == MdKind.Rule) return new Size(maxW, Theme.K(9));

            var font = FontFor(b, system);
            int avail = maxW;

            if (b.Kind == MdKind.Code)
                avail = Math.Max(Theme.K(40), maxW - Theme.K(CodePadX) * 2);

            int indent = 0;
            if (b.Kind == MdKind.ListItem) indent = Theme.K(16);
            if (b.Kind == MdKind.Quote) indent = Theme.K(10);
            avail = Math.Max(Theme.K(30), avail - indent);

            // 快速路径：不含换行的短文本先按单行量。
            //
            // `TextFormatFlags.WordBreak` 会走 GDI+ 的断行算法（要算每个可能的
            // 换行点），比 `SingleLine` 贵不少。而一条消息里的块大多数是
            // 单行的（列表项、短段落、标题）——先量单行，宽度够就直接用，
            // 不够再走 WordBreak。
            Size sz;
            bool single = DisplayText(b).IndexOf('\n') < 0;
            if (single)
            {
                sz = TextRenderer.MeasureText(DisplayText(b), font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (sz.Width > avail)
                {
                    sz = TextRenderer.MeasureText(DisplayText(b), font, new Size(avail, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
            }
            else
            {
                sz = TextRenderer.MeasureText(DisplayText(b), font, new Size(avail, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }

            int w = sz.Width + indent;
            int h = sz.Height;
            if (b.Kind == MdKind.Code) { w += Theme.K(CodePadX) * 2; h += Theme.K(CodePadY) * 2; }
            if (b.Kind == MdKind.Heading) h += Theme.K(2);   // 标题上方留一点气口

            return new Size(Math.Min(w, maxW), h);
        }

        // ===================== 交互 =====================

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

        // ===================== 绘制 =====================

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 系统消息：不加气泡形状，只铺一层浅底 + 小字
            if (_role == BubbleRole.System)
            {
                var sr = new Rectangle(0, 0, Width, Height);
                using (var b = new SolidBrush(Theme.SurfaceAlt))
                    g.FillRectangle(b, sr);
                var usable = new Rectangle(Theme.K(6), Theme.K(6), Width - Theme.K(12), Height - Theme.K(12));
                PaintBlocks(g, _blocks, usable, true);
                return;
            }

            bool user = _role == BubbleRole.User;

            // 气泡本体：指向角在说话人一侧；助手气泡左边还要留出徽章位
            int padLeft = Theme.K(user ? 0 : SealW + SealGap);
            int padRight = Theme.K(user ? TailW : 0);
            var box = new Rectangle(padLeft, 0, Width - padLeft - padRight - 1, Height - 1);
            if (box.Width <= Theme.K(4) || box.Height <= Theme.K(4)) return;

            // 底色 = 强调色 11%(用户) / 9%(助手) 混在 Surface 上
            Color tint = user ? Theme.Accent : Theme.Violet;
            Color bg = Theme.Mix(tint, Theme.Surface, user ? 0.11 : 0.09);
            Color edge = Theme.Mix(tint, Theme.Line, _hover ? 0.55 : 0.30);

            // 非对称圆角：说话人那一侧的角收小到 4px
            int big = Theme.K(13), small = Theme.K(4);
            using (var path = user
                ? Theme.RoundPathCorners(box, big, big, small, big)     // 右下=4
                : Theme.RoundPathCorners(box, small, big, big, big))    // 左上=4
            using (var b = new SolidBrush(bg))
            using (var pen = new Pen(edge, Theme.Kf(1)))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }

            DrawTail(g, box, bg, edge, user);
            if (!user) DrawSeal(g);

            var inner = new Rectangle(
                box.X + Theme.K(PadX), box.Y + Theme.K(PadY),
                box.Width - Theme.K(PadX) * 2, box.Height - Theme.K(PadY) * 2);
            if (inner.Width > Theme.K(4) && inner.Height > Theme.K(4))
                PaintBlocks(g, _blocks, inner, false);
        }

        /// <summary>按块依次绘制。`origin` 是内容区左上角。</summary>
        private void PaintBlocks(Graphics g, List<MdBlock> blocks, Rectangle area, bool system)
        {
            int y = area.Y;
            bool first = true;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                // 用构造时缓存的尺寸；万一数量对不上（不该发生）才退回现算
                var sz = (i < _blockSizes.Length)
                    ? _blockSizes[i]
                    : MeasureBlock(b, area.Width, system);
                if (!first) y += Theme.K(BlockGap);
                first = false;

                var slot = new Rectangle(area.X, y, area.Width, sz.Height);

                switch (b.Kind)
                {
                    case MdKind.Rule:
                        using (var pen = new Pen(Theme.LineDark, Theme.Kf(1)))
                            g.DrawLine(pen, slot.X, slot.Y + slot.Height / 2,
                                slot.Right, slot.Y + slot.Height / 2);
                        break;

                    case MdKind.Code:
                        PaintCode(g, b, new Rectangle(slot.X, slot.Y, sz.Width, sz.Height));
                        break;

                    case MdKind.Quote:
                        using (var bar = new SolidBrush(Theme.Violet))
                            g.FillRectangle(bar, slot.X, slot.Y + Theme.K(1), Theme.K(2), Math.Max(Theme.K(4), slot.Height - Theme.K(2)));
                        TextRenderer.DrawText(g, DisplayText(b), FontFor(b, system),
                            new Rectangle(slot.X + Theme.K(10), slot.Y, slot.Width - Theme.K(10), slot.Height),
                            Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                        break;

                    case MdKind.ListItem:
                        TextRenderer.DrawText(g, string.IsNullOrEmpty(b.Lang) ? "·" : b.Lang,
                            FontFor(b, system), new Rectangle(slot.X, slot.Y, Theme.K(14), slot.Height),
                            system ? Theme.Muted : Theme.Accent,
                            TextFormatFlags.Left | TextFormatFlags.NoPadding);
                        TextRenderer.DrawText(g, DisplayText(b), FontFor(b, system),
                            new Rectangle(slot.X + Theme.K(16), slot.Y, slot.Width - Theme.K(16), slot.Height),
                            system ? Theme.Muted : Theme.Text,
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                        break;

                    case MdKind.Heading:
                        TextRenderer.DrawText(g, DisplayText(b),
                            FontFor(b, system),
                            new Rectangle(slot.X, slot.Y, slot.Width, slot.Height),
                            system ? Theme.Muted : Theme.Text,
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                        break;

                    default:
                        // 绘制字体必须与 MeasureBlock 的 FontFor 一致：
                        // 系统气泡量的是 FontSmall，画成 FontBody 就会裁掉下半行
                        TextRenderer.DrawText(g, DisplayText(b), FontFor(b, system),
                            new Rectangle(slot.X, slot.Y, slot.Width, slot.Height),
                            system ? Theme.Muted : Theme.Text,
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                        break;
                }

                y += sz.Height;
            }
        }

        /// <summary>
        /// 代码块：深一层的圆角底 + 等宽字体。
        ///
        /// 底色用 `SurfaceAlt`，在气泡的淡色雾上再压一层，视觉上「沉下去」——
        /// 和 web 版 `<pre>` 的处理一致。语言标记不画（省一行高度），
        /// 内容加了上下内边距所以不会贴边。
        /// </summary>
        private static void PaintCode(Graphics g, MdBlock b, Rectangle slot)
        {
            using (var path = Theme.RoundPath(slot, Theme.K(6)))
            using (var fill = new SolidBrush(Theme.SurfaceAlt))
            using (var pen = new Pen(Theme.LineDark, Theme.Kf(1)))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            var inner = new Rectangle(
                slot.X + Theme.K(CodePadX), slot.Y + Theme.K(CodePadY),
                Math.Max(Theme.K(10), slot.Width - Theme.K(CodePadX) * 2),
                Math.Max(Theme.K(10), slot.Height - Theme.K(CodePadY) * 2));
            TextRenderer.DrawText(g, DisplayText(b), Theme.FontMono, inner,
                Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// 「灵枢」标识徽章：气泡左边一个 25×25 的圆角方块，紫色描边 + 紫色「灵」字。
        ///
        /// 对应 web 版的 `.seal`。作用是**让助手的气泡有个身份标记**——
        /// 一屏里来回看的时候，不用读内容就知道哪条是它说的。
        /// </summary>
        private void DrawSeal(Graphics g)
        {
            var seal = new Rectangle(0, Theme.K(5), Theme.K(SealW), Theme.K(SealW));
            if (seal.Right > Width || seal.Bottom > Height) return;

            Color border = Theme.Mix(Theme.Violet, Theme.Line, 0.62);
            using (var path = Theme.RoundPath(seal, Theme.K(6)))
            using (var b = new SolidBrush(Theme.Surface))
            using (var pen = new Pen(border, Theme.Kf(1.5f)))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, "灵", SealFont, seal, Theme.Violet,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        /// <summary>
        /// 指向角：一个小三角从气泡边上探出去。
        ///
        /// web 版是「9×9 方块旋转 45°、压住气泡边」，视觉上等于一个等腰三角形。
        /// 这里直接画三角形，省掉旋转和裁切的麻烦。
        /// 位置对齐 web：用户气泡在右侧、距底 11px 起；助手气泡在左侧、距顶 12px 起。
        /// </summary>
        private static void DrawTail(Graphics g, Rectangle box, Color bg, Color edge, bool user)
        {
            int size = Theme.K(9);
            int tailW = Theme.K(TailW);
            int top = user
                ? Math.Max(box.Y + Theme.K(8), box.Bottom - Theme.K(11) - size)
                : box.Y + Theme.K(12);

            PointF[] tri;
            if (user)
            {
                float edgeX = box.Right;
                tri = new PointF[]
                {
                    new PointF(edgeX, top),
                    new PointF(edgeX + tailW - 0.5f, top + size / 2f),
                    new PointF(edgeX, top + size),
                };
            }
            else
            {
                float edgeX = box.Left;
                tri = new PointF[]
                {
                    new PointF(edgeX, top),
                    new PointF(edgeX - tailW + 0.5f, top + size / 2f),
                    new PointF(edgeX, top + size),
                };
            }

            using (var b = new SolidBrush(bg))
                g.FillPolygon(b, tri);
            // 只描外侧那两条边（内边贴在气泡上，描了会出现一道缝）
            using (var pen = new Pen(edge, Theme.Kf(1)))
            {
                g.DrawLine(pen, tri[0], tri[1]);
                g.DrawLine(pen, tri[1], tri[2]);
            }
        }
    }
}
