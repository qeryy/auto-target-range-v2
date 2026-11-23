// 灵枢桌面版（原生）—— 数据表格
//
// 为什么用 DataGridView 而不是 ListView：web 端各页都是「按列排序 + 状态着色 +
// 点击进详情」，ListView 要做这些得自己画。DataGridView 自带排序、单元格样式、
// 虚拟行，样式调好之后观感和 web 端的表格接近。
//
// 这里把「深色表格怎么设」收在一处：以后调风格只改这个文件。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SpiritScout.Native
{
    public sealed class DataGrid : DataGridView
    {
        public DataGrid()
        {
            // 基础观感
            BackgroundColor = UiKit.Bg;
            BorderStyle = BorderStyle.None;
            GridColor = UiKit.Line;
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
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
            ColumnHeadersHeight = 36;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            RowTemplate.Height = 34;
            Font = UiKit.FontBody;
            DoubleBuffered = true;

            // 表头
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = UiKit.Surface,
                ForeColor = UiKit.Muted,
                SelectionBackColor = UiKit.Surface,
                SelectionForeColor = UiKit.Muted,
                Font = UiKit.FontSmall,
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
            };

            // 单元格
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = UiKit.Surface,
                ForeColor = UiKit.Text,
                SelectionBackColor = UiKit.SurfaceHover,
                SelectionForeColor = UiKit.Text,
                Padding = new Padding(8, 0, 0, 0),
            };

            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(19, 24, 31),
                ForeColor = UiKit.Text,
                SelectionBackColor = UiKit.SurfaceHover,
                SelectionForeColor = UiKit.Text,
                Padding = new Padding(8, 0, 0, 0),
            };
        }

        /// <summary>加一列（从左到右顺序调用）。</summary>
        public void AddColumn(string key, string header, float fillWeight = 1f,
            DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = key,
                HeaderText = header,
                FillWeight = fillWeight,
                SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = align,
                    Padding = new Padding(8, 0, 0, 0),
                },
            };
            Columns.Add(col);
        }

        /// <summary>一次性填入行（先清空）。</summary>
        public void Fill(IEnumerable<Dictionary<string, object>> rows)
        {
            Rows.Clear();
            if (rows == null) return;
            foreach (var row in rows)
            {
                int idx = Rows.Add();
                var cells = Rows[idx].Cells;
                foreach (DataGridViewColumn col in Columns)
                {
                    if (col.Index >= cells.Count) continue;
                    object v;
                    cells[col.Index].Value = row.TryGetValue(col.Name, out v) && v != null
                        ? v.ToString()
                        : "";
                }
            }
        }

        /// <summary>给某列的某些值着色（比如 phase=completed 显示绿色）。</summary>
        public void ColorizeCells(string columnKey, Func<string, Color?> picking)
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
    }
}
