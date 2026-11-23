// 灵枢桌面版 —— 界面健壮性工具
//
// 两件容易出事的界面细节，收在一处：
//
// 1. **SplitContainer.SplitterDistance 会抛异常**
//    如果给的值超过「可用尺寸 - 25」，WinForms 直接抛 InvalidOperationException，
//    表现为「窗口一缩小就崩」。这里按容器实际尺寸夹取，并在尺寸变化时重新夹。
//
// 2. **窗口尺寸变化时子控件的布局**
//    有些页面在构造时按 ClientSize 摆了按钮，窗口一变大就对不齐。
//    统一用 Anchor 或 Dock，别在构造时算绝对坐标。

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Ui
{
    public static class UiSafety
    {
        /// <summary>
        /// 安全地设置分隔位置。超过可用范围就夹到边界内，并在容器尺寸变化时重新夹。
        /// </summary>
        public static void SafeSplit(SplitContainer split, int wanted)
        {
            if (split == null) return;

            Action apply = () =>
            {
                if (Theme.IsScaling) return;
                try
                {
                    bool vertical = split.Orientation == Orientation.Vertical;
                    int total = vertical ? split.Width : split.Height;
                    // WinForms 的硬限制：距离必须落在 [25, 总长-25] 内
                    int min = 25;
                    int max = Math.Max(min + 1, total - 25);
                    int target = (int)Math.Round(wanted * Theme.UnitOf(split));
                    if (target < min) target = min;
                    if (target > max) target = max;
                    split.SplitterDistance = target;
                }
                catch
                {
                    // 容器还没成形时设不上，忽略——尺寸变化后还会再来一次
                }
            };

            apply();
            split.SizeChanged += (s, e) => apply();
            split.HandleCreated += (s, e) => apply();
        }

        /// <summary>
        /// 统一的确认框（危险操作）。返回 true 表示用户确认。
        /// </summary>
        public static bool Confirm(IWin32Window owner, string message, string title = "确认")
        {
            return MessageBox.Show(owner, message, title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>
        /// 起一个异步任务，失败时把错误报出来。
        ///
        /// **为什么需要这个**：`task.GetAwaiter()` 看起来像在等任务，
        /// 其实不会——它只返回 awaiter，任务失败了异常就静默消失。
        /// 页面上那些 `var t = RefreshAsync(); t.GetAwaiter();` 全是这种情况：
        /// 刷新失败时用户只看到「点了没反应」。
        ///
        /// 这个辅助给任务挂一个只处理失败的延续：记日志 + 弹一次提示。
        /// 连续失败不重复弹窗（用 _lastError 去重），避免刷屏。
        /// </summary>
        public static void Fire(Task task, string what)
        {
            if (task == null) return;
            task.ContinueWith(t =>
            {
                if (!t.IsFaulted || t.Exception == null) return;
                var ex = t.Exception.GetBaseException();
                string key = what + "|" + ex.GetType().Name + "|" + ex.Message;
                if (key == _lastError) return;      // 同一条错误不连弹
                _lastError = key;

                try { Core.ConfigManager.Log("[" + what + "] " + ex); } catch { }
                try
                {
                    MessageBox.Show("「" + what + "」失败：\n\n" + ex.Message
                        + "\n\n详情已写入日志目录。",
                        "灵枢 · SpiritScout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }

        private static string _lastError;

        /// <summary>
        /// 从一个控件往上找宿主窗体。
        /// </summary>
        public static Form ShellOf(Control c)
        {
            while (c != null)
            {
                var f = c as Form;
                if (f != null) return f;
                c = c.Parent;
            }
            return null;
        }
    }
}
