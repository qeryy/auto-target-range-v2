// 灵枢桌面版 —— 数据表格
//
// WinForms 的 DataGridView 默认是亮色表格，这里统一调成深色、行高 34、
// 表头小字、隔行微差。各页都从这里拿，风格自然一致。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class DataTable : DataGridView
    {
        /// <summary>斑马纹：只比卡面亮一点点，扫长表能跟上行又不显「格子」。</summary>
        private static readonly Color Zebra = Theme.Mix(Theme.Surface, Theme.SurfaceAlt, 0.45);
        private static readonly Color HoverSoft = Theme.Mix(Theme.Surface, Theme.Hover, 0.55);

        public DataTable()
        {
            // 表格的「空白区」用 Surface 而不是 Bg。
            //
            // 原来 BackgroundColor = Bg（#0d1117），和页面背景一模一样 ——
            // 数据为空时整页就是一大片同样的深色，**看不出这里有个表格**。
            // 而单元格的 BackColor 是 Surface（#161b22），所以是
            // 「有数据时浅、没数据时深」，边界忽有忽无。
            //
            // 统一成 Surface 之后，表格区域始终比页面背景亮一档，
            // 一打开就知道「数据应该出现在这块」。
            BackgroundColor = Theme.Surface;
            BorderStyle = BorderStyle.None;
            GridColor = Theme.Surface;
            // 软表格：**不画网格线**。行与行靠留白和极淡斑马纹区分，
            // 外面再套板块卡的留白——「方框感」主要就是这些 1px 线给的。
            CellBorderStyle = DataGridViewCellBorderStyle.None;
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            EnableHeadersVisualStyles = false;
            RowHeadersVisible = false;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            ReadOnly = true;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            MultiSelect = false;
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            // 行高/表头高/单元格 Padding 不被 Control.Scale 缩放，直接给物理值
            ColumnHeadersHeight = Theme.K(32);
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            RowTemplate.Height = Theme.K(36);
            Font = Theme.FontBody;
            DoubleBuffered = true;

            // 表头与数据同底，靠「加粗小字 + 淡色」区分，不再是一条深色横梁
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Muted,
                SelectionBackColor = Theme.Surface,
                SelectionForeColor = Theme.Muted,
                Font = Theme.FontSection,
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(Theme.K(12), 0, Theme.K(8), 0),
            };

            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                SelectionBackColor = Theme.Mix(Theme.Accent, Theme.Surface, 0.14),
                SelectionForeColor = Theme.Text,
                Padding = new Padding(Theme.K(12), 0, Theme.K(8), 0),
            };

            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Zebra,
                ForeColor = Theme.Text,
                SelectionBackColor = Theme.Mix(Theme.Accent, Theme.Surface, 0.14),
                SelectionForeColor = Theme.Text,
                Padding = new Padding(Theme.K(12), 0, Theme.K(8), 0),
            };

            // 鼠标划过时高亮那一行。
            //
            // 之前只有「选中」高亮。表格是这个软件的主要交互区（13 个页面里 9 个是表格），
            // 鼠标移上去没有任何变化的话，用户不知道光标落在哪一行 —— 尤其行高 34、
            // 一屏十几行的时候，眼神要来回找。
            //
            // 处理上要避开两件事：
            //   · 不要动**选中行** —— 它的 SelectionBackColor 会盖过去，但恢复时
            //     如果直接写 BackColor 会和斑马行打架
            //   · 不要和**斑马行**同色 —— SurfaceAlt 已经是斑马色了，hover 用它会看不出变化
            CellMouseEnter += DataTable_CellMouseEnter;
            CellMouseLeave += DataTable_CellMouseLeave;
            RowsRemoved += DataTable_RowsRemoved;
        }

        private int _hoverRow = -1;

        private void DataTable_CellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            SetHoverRow(e.RowIndex);
        }

        private void DataTable_CellMouseLeave(object sender, EventArgs e)
        {
            SetHoverRow(-1);
        }

        private void DataTable_RowsRemoved(object sender, DataGridViewRowsRemovedEventArgs e)
        {
            // 行被删掉之后行号全变了，之前记的 _hoverRow 指向别的行
            _hoverRow = -1;
        }

        private void SetHoverRow(int row)
        {
            if (_hoverRow == row) return;
            RestoreHover(_hoverRow);
            _hoverRow = row;
            if (row >= 0 && row < Rows.Count)
                Rows[row].DefaultCellStyle.BackColor = HoverSoft;
        }

        private void RestoreHover(int row)
        {
            if (row < 0 || row >= Rows.Count) return;
            // 恢复成「该行本来该有的颜色」：偶数行 Surface，奇数行斑马
            Rows[row].DefaultCellStyle.BackColor =
                (row % 2 == 1) ? Zebra : Theme.Surface;
        }

        public void AddColumn(string key, string header, float weight = 1f,
            DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = key,
                HeaderText = header,
                FillWeight = weight,
                SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = align,
                    Padding = new Padding(Theme.K(12), 0, Theme.K(8), 0),
                },
            });
        }

        /// <summary>
        /// 表格为空时在中央显示的说明。传空串表示不显示。
        ///
        /// 各页把「还没有任务」这类提示写在底部状态栏里——那行字太轻，
        /// 用户第一眼看到的是一张空表格，容易以为程序没加载好。
        ///
        /// **画在表格正中间**比写在状态栏强：一眼能认出「这里本来就是空的」。
        ///
        /// 实现上没有用「往 DataGridView 里塞 Label」——那是原生控件，
        /// 子控件会被单元格层盖住（试过四种方式都不行）。改成在 OnPaint
        /// 里、**base.OnPaint 之后**用 e.Graphics 直接画：那时背景已经填好，
        /// 画上去就在最上层。
        /// </summary>
        public string EmptyText
        {
            get { return _emptyText; }
            set
            {
                if (_emptyText == value) return;
                _emptyText = value ?? "";
                Invalidate();
            }
        }

        private string _emptyText = "";

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);   // 先让 DataGridView 把背景和单元格画完

            // 不画外边框：表格套在板块卡里，卡面就是它的边界。
            if (Rows.Count > 0 || string.IsNullOrEmpty(_emptyText)) return;

            // 只在「真的没有行」时画。有行但不显示（比如过滤后为空）也画——
            // 那种情况用户更需要提示。
            var area = new Rectangle(0, ColumnHeadersHeight, Width, Height - ColumnHeadersHeight);
            if (area.Height < Theme.K(40)) return;

            // 空状态画成一个「虚线框 + 浅底」的占位块，而不是一行飘在黑色里的字。
            // 对应 web 版 polish.css 里的：
            //   .empty { border: 1px dashed var(--line-strong); background: var(--surface-2); }
            //
            // 占位块留出边距，别贴着表格边框——贴边会让它看起来像「表格的一部分」。
            // **占位块要小、要居中，不能撑满整个表格。**
            //
            // 原来 box 是 `area` 减 16px 边距 —— 等于**整块表格都填成 SurfaceAlt**。
            // 2026-11-19 截图分析发现：空表首页有 **53% 的屏幕是一大块浅灰圆角**，
            // 中间飘着一行小字。这不是配色问题，是**一块大色块压在那儿**。
            //
            // 现在固定成 420×120 以内、水平居中、垂直略偏上（1/3 处），
            // 周围留出页面背景 —— 它看起来才像「一个提示」，而不是「一片面板」。
            int boxW = Math.Min(Theme.K(420), area.Width - Theme.K(32));
            int boxH = Math.Min(Theme.K(120), area.Height - Theme.K(24));
            if (boxW < Theme.K(120) || boxH < Theme.K(56)) return;
            var box = new Rectangle(
                area.X + (area.Width - boxW) / 2,
                area.Y + Math.Max(Theme.K(20), (area.Height - boxH) / 3),
                boxW, boxH);

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 浅底：**比表格底色只亮一点点**，让虚线框当主角。
            // 原来直接用 SurfaceAlt（比卡片亮两级），一整块下来很闷。
            using (var path = Theme.RoundPath(box, Theme.K(Theme.RadiusLg)))
            using (var b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.SurfaceAlt, 0.4)))
            {
                e.Graphics.FillPath(b, path);
            }
            // 虚线框
            using (var path = Theme.RoundPath(box, Theme.K(Theme.RadiusLg)))
            using (var pen = new Pen(Theme.LineDark, Theme.Kf(1)))
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                pen.DashPattern = new float[] { Theme.Kf(4), Theme.Kf(3) };
                e.Graphics.DrawPath(pen, path);
            }

            TextRenderer.DrawText(
                e.Graphics,
                _emptyText,
                Theme.FontBody,
                box,
                Theme.Muted,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.WordBreak
                | TextFormatFlags.NoPrefix);
        }

        /// <summary>填入行（先清空）。行是 key→值 的字典。</summary>
        private string _fillFingerprint;

        /// <summary>
        /// 填数据。**行数和内容都没变时直接跳过**，不清空重建。
        ///
        /// 原来每次 Refresh 都 `Rows.Clear()` + `Rows.Add(N)` —— 数据没变也全表重建，
        /// 切页/刷新时会肉眼看到表格闪一下。
        /// 现在先算一遍指纹（行数 + 每行每列的首格值拼接），一样就直接 return。
        /// 这个检查是 O(rows × cols)，只比 Clear 快一点，
        /// 但**省掉的是整张表的控件重建和重绘**，那才是真正贵的部分。
        /// </summary>
        public void Fill(IEnumerable<Dictionary<string, object>> rows)
        {
            if (rows == null)
            {
                if (Rows.Count == 0) return;
                _fillFingerprint = "";
                Rows.Clear();
                return;
            }

            // 指纹：行数 + 每行首列值拼一起（足够分辨「变没变」）
            int rowCount = 0;
            var fp = new System.Text.StringBuilder(64);
            foreach (var r in rows)
            {
                rowCount++;
                // 每行取第一个字段的值当指纹 —— 不逐格比，成本才低
                foreach (var kv in r)
                {
                    fp.Append(kv.Value == null ? "" : kv.Value.ToString());
                    break;
                }
                fp.Append('|');
            }
            fp.Append(rowCount);

            if (fp.ToString() == _fillFingerprint && Rows.Count == rowCount)
                return;   // 数据没变，别动

            _fillFingerprint = fp.ToString();
            Rows.Clear();

            // 两个提速手段：
            //   1. SuspendLayout —— 加行期间别重排
            //   2. Rows.Add(object[]) —— 一次把整行值传进去，
            //      而不是 Rows.Add() 之后再逐个 cells[i].Value = ...
            //      （后者每赋一个单元格都可能触发一次单行重绘）
            SuspendLayout();
            try
            {
                int colCount = Columns.Count;
                var cells = new object[colCount];
                foreach (var row in rows)
                {
                    for (int i = 0; i < colCount; i++)
                    {
                        object v;
                        string name = Columns[i].Name;
                        cells[i] = row.TryGetValue(name, out v) && v != null ? v.ToString() : "";
                    }
                    Rows.Add(cells);
                }
            }
            finally
            {
                ResumeLayout();
            }

            // 行数变了，空状态提示的显示/隐藏要跟着变
            if (Rows.Count == 0 && !string.IsNullOrEmpty(_emptyText)) Invalidate();
        }

        /// <summary>按值给某一列着色。</summary>
        public void Colorize(string columnKey, Func<string, Color?> picking)
        {
            foreach (DataGridViewRow row in Rows)
            {
                foreach (DataGridViewCell cell in row.Cells)
                {
                    if (Columns[cell.ColumnIndex].Name != columnKey) continue;
                    var text = cell.Value == null ? "" : cell.Value.ToString();
                    var color = picking(text);
                    if (color.HasValue)
                    {
                        cell.Style.ForeColor = color.Value;
                        cell.Style.SelectionForeColor = color.Value;
                    }
                }
            }
        }

        // ===== 胶囊标签列 =====
        //
        // web 版的「状态」是「浅色底 + 同色字」的圆角胶囊。DataGridView 的单元格
        // 只能填矩形底色，做不出圆角，所以这里用 CellPainting 自绘：
        //   1. 先让 base 画背景和选中态
        //   2. 再在这个单元格里画一个居中的圆角小胶囊
        //   3. e.Handled = true 阻止默认文字绘制，自己画进去
        //
        // 列宽不够放胶囊时（< 40px）退回普通文字，不画。
        private readonly HashSet<string> _chipColumns = new HashSet<string>();

        /// <summary>把某列渲染成胶囊标签。配合 Colorize 用（Colorize 给前景色，
        /// 这里取它的 ForeColor 反过来算背景的柔和版）。</summary>
        public void MakeChipColumn(string columnKey)
        {
            if (string.IsNullOrEmpty(columnKey)) return;
            _chipColumns.Add(columnKey);
        }

        protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
        {
            base.OnCellPainting(e);
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (!_chipColumns.Contains(Columns[e.ColumnIndex].Name)) return;
            if (e.Graphics == null) return;

            var cell = Rows[e.RowIndex].Cells[e.ColumnIndex];
            string text = cell.Value == null ? "" : cell.Value.ToString();
            if (text.Length == 0) return;

            var cellRect = e.CellBounds;
            if (cellRect.Width < Theme.K(40) || cellRect.Height < Theme.K(14)) return;

            Color fore = cell.Style.ForeColor;
            if (fore.IsEmpty) fore = Theme.Text;

            // 背景 = 前景色 12% alpha（相当于 web 的 --xxx-soft）
            Color back = Color.FromArgb(30, fore.R, fore.G, fore.B);

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 先按当前单元格底色铺一遍（选中/悬停时底色不同，不能一律用 Surface）
            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            Color fill;
            if (selected)
            {
                var sb = DefaultCellStyle.SelectionBackColor;
                fill = sb.IsEmpty ? Theme.AccentSoft : sb;
            }
            else
            {
                var cb = cell.InheritedStyle.BackColor;
                fill = cb.IsEmpty ? BackColor : cb;
            }
            using (var bg = new SolidBrush(fill))
            {
                e.Graphics.FillRectangle(bg, cellRect);
            }

            var size = TextRenderer.MeasureText(e.Graphics, text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding);
            int padH = Theme.K(9), padV = Theme.K(3);
            int w = Math.Min(size.Width + padH * 2, cellRect.Width - Theme.K(6));
            int h = Math.Min(size.Height + padV * 2, cellRect.Height - Theme.K(6));
            if (w < Theme.K(16) || h < Theme.K(10)) return;

            var pill = new Rectangle(
                cellRect.X + (cellRect.Width - w) / 2,
                cellRect.Y + (cellRect.Height - h) / 2,
                w, h);

            using (var path = Theme.RoundPath(pill, Theme.RadiusPill))
            using (var b = new SolidBrush(back))
            {
                e.Graphics.FillPath(b, path);
            }

            TextRenderer.DrawText(e.Graphics, text, Font, pill, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            e.Handled = true;
        }

        /// <summary>取某行的某列文本。</summary>
        public string CellText(int rowIndex, string columnKey)
        {
            if (rowIndex < 0 || rowIndex >= Rows.Count) return "";
            var cell = Rows[rowIndex].Cells[columnKey];
            return cell == null || cell.Value == null ? "" : cell.Value.ToString();
        }
    }
}
