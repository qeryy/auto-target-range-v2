// 灵枢桌面版 —— 日志查看器
//
// 之前「设置 → 运行日志」只有「打开目录」和「清空」两个按钮。
// 看日志得切到记事本：那不是深色，几百行一行行翻，还容易改坏文件。
//
// 这个对话框把当天日志的最后 N 行直接摊在程序里：
//   · 深色，和主界面一致
//   · 只读，不会误改
//   · 高亮包含「失败 / 错误 / Exception」的行
//   · 可一键复制全部（贴给别人看）
//
// 设计上取自废弃的 `launcher/native/BackendService.TailLog`——那版会在启动遮罩
// 里实时滚后端日志。我的版本没有后端进程，但「在程序里看日志」这件事一样成立。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SpiritScout.Ui.Pages
{
    public sealed class LogViewer : Form
    {
        private readonly TextBox _box;
        private readonly Label _stat;

        public LogViewer()
        {
            Text = "运行日志";
            // 和其他 11 个窗口保持一致：Win11 原生圆角。
            // 这里原本漏了 —— 打开「运行日志」会看到一个直角窗口，
            // 而其它对话框都是圆角的，一眼就能看出不配套。
            Theme.ApplyWin11(this);
            ClientSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;
            MinimizeBox = false;

            _box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Font = Theme.FontMono,
                Dock = DockStyle.Fill,
            };

            _stat = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
            };

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.Bg, Padding = new Padding(10, 6, 10, 6) };

            var refresh = Theme.Ghost("刷新", 76, 32);
            refresh.Location = new Point(10, 7);
            refresh.Click += (s, e) => Reload();
            bar.Controls.Add(refresh);

            var copy = Theme.Ghost("复制全部", 92, 32);
            copy.Location = new Point(94, 7);
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(_box.Text); _stat.Text = "已复制 " + _box.Lines.Length + " 行"; }
                catch { }
            };
            bar.Controls.Add(copy);

            var openDir = Theme.Ghost("打开目录", 92, 32);
            openDir.Location = new Point(194, 7);
            openDir.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Core.AppPaths.LogDir + "\""); }
                catch { }
            };
            bar.Controls.Add(openDir);

            var close = Theme.Primary("关闭", 76, 32);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Location = new Point(ClientSize.Width - 86, 7);
            close.Click += (s, e) => Close();
            bar.Controls.Add(close);

            Controls.Add(_box);
            Controls.Add(bar);
            Controls.Add(_stat);

            Reload();
        }

        /// <summary>读当天日志的最后 2000 行。</summary>
        private void Reload()
        {
            try
            {
                string file = Path.Combine(Core.AppPaths.LogDir,
                    "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                if (!File.Exists(file))
                {
                    _box.Text = "今天还没有日志。\r\n\r\n" + file;
                    _stat.Text = file;
                    return;
                }

                // 不用 File.ReadAllLines 整个读进来——日志轮转前可能有 8MB。
                // 这里用一个环形缓冲只留最后 2000 行。
                const int keep = 2000;
                var tail = new Queue<string>(keep);
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (tail.Count >= keep) tail.Dequeue();
                        tail.Enqueue(line);
                    }
                }

                var sb = new StringBuilder();
                int errCount = 0;
                foreach (var line in tail)
                {
                    sb.AppendLine(line);
                    if (LooksLikeError(line)) errCount++;
                }

                // 去掉末尾的换行——不然 TextBox.Lines 会多出一个空行，
                // 状态栏写「2000 行」而实际数出来是 2001，看着像 bug。
                _box.Text = sb.ToString().TrimEnd('\r', '\n');
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();

                _stat.Text = Path.GetFileName(file) + "  ·  最后 " + tail.Count + " 行"
                    + (errCount > 0 ? "  ·  其中 " + errCount + " 行含错误关键字" : "")
                    + "  ·  " + new FileInfo(file).Length / 1024 + " KB";
                _stat.ForeColor = errCount > 0 ? Theme.Warn : Theme.Muted;
            }
            catch (Exception ex)
            {
                _box.Text = "读日志失败：\r\n" + ex;
                _stat.Text = "读取失败";
                _stat.ForeColor = Theme.Danger;
            }
        }

        private static bool LooksLikeError(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.IndexOf("失败", StringComparison.Ordinal) >= 0
                || line.IndexOf("错误", StringComparison.Ordinal) >= 0
                || line.IndexOf("异常", StringComparison.Ordinal) >= 0
                || line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
