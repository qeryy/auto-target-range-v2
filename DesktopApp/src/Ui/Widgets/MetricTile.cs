// 灵枢桌面版 —— 指标软块（仪表盘 / 用量共用）
//
// 一张色晕软卡：分类色图标块 + 指标名 + 大数字。
// 四个指标四种色晕，一眼分开「会话 / 消息 / Token / 成本」这类不同口径。

using System.Drawing;
using System.Windows.Forms;

namespace SpiritScout.Ui.Widgets
{
    public sealed class MetricTile : Card
    {
        /// <summary>大数字（调用方刷新时改 Text）。</summary>
        public Label Value { get; private set; }

        public MetricTile(string label, Color hue, string glyph)
        {
            Tint = hue;
            Controls.Add(new HueTile
            {
                Hue = hue,
                Glyph = glyph,
                Size = new Size(28, 28),
                Location = new Point(16, 14),
            });
            Controls.Add(new Label
            {
                Text = label,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                Location = new Point(52, 20),
                BackColor = Color.Transparent,
            });
            Value = new Label
            {
                Text = "—",
                ForeColor = Theme.Text,
                Font = Theme.FontMetric,
                AutoSize = true,
                Location = new Point(16, 48),
                BackColor = Color.Transparent,
            };
            Controls.Add(Value);
        }
    }
}