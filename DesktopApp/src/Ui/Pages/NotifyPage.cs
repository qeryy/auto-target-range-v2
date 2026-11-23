// 灵枢桌面版 —— 页面：通知
//
// 桌面版的通知比 web 版更有意义：web 版只能页面内提示，桌面版可以弹系统气泡，
// 长任务跑完时人可以去干别的。
//
// 三条开关：
//   · 任务完成时（phase 变 completed）
//   · 任务失败时（failed / stopped）
//   · 需要你拍板时（agent 提问或审批）
//
// 历史存 notifications.json，最多留最近 100 条。
//
// 实现说明：WinForms 用 NotifyIcon 弹气球提示（托盘区气泡）。这是 Windows 上
// 无需额外依赖的通知方式——不引 Windows.UI.Notifications，保持零依赖。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    /// <summary>通知配置。</summary>
    public sealed class NotifyConfig
    {
        public bool OnComplete { get; set; }
        public bool OnFailure { get; set; }
        public bool OnQuestion { get; set; }
        public bool SoundEnabled { get; set; }

        public NotifyConfig()
        {
            OnComplete = true;
            OnFailure = true;
            OnQuestion = true;
            SoundEnabled = false;
        }
    }

    /// <summary>一条通知记录。</summary>
    public sealed class NotifyEntry
    {
        public string Title { get; set; }
        public string Body { get; set; }
        public string Kind { get; set; }      // info / ok / warn
        public DateTime At { get; set; }

        public NotifyEntry() { Title = ""; Body = ""; Kind = "info"; At = DateTime.Now; }
    }

    public sealed class NotifyFile
    {
        public List<NotifyEntry> History { get; set; }
        public NotifyFile() { History = new List<NotifyEntry>(); }
    }

    /// <summary>
    /// 通知中心：页面用它展示配置与历史，其他代码用它发通知。
    /// 用静态状态是为了让「发通知」这件事在任何地方都能一句调用。
    /// </summary>
    public static class NotifyCenter
    {
        private static NotifyIcon _icon;
        private static NotifyConfig _config;

        public static string ConfigPath
        {
            get { return Path.Combine(Core.AppPaths.DataRoot, "notify.json"); }
        }

        public static string HistoryPath
        {
            get { return Path.Combine(Core.AppPaths.DataRoot, "notifications.json"); }
        }

        public static NotifyConfig Config
        {
            get
            {
                if (_config == null)
                    _config = Core.JsonStore.Read<NotifyConfig>(ConfigPath, null) ?? new NotifyConfig();
                return _config;
            }
        }

        public static void SaveConfig()
        {
            if (_config != null) Core.JsonStore.Write(ConfigPath, _config);
        }

        /// <summary>发一条通知（写历史 + 弹气泡）。</summary>
        public static void Push(string title, string body, string kind = "info")
        {
            var file = Core.JsonStore.Read<NotifyFile>(HistoryPath, null) ?? new NotifyFile();
            if (file.History == null) file.History = new List<NotifyEntry>();
            file.History.Insert(0, new NotifyEntry
            {
                Title = title,
                Body = body,
                Kind = kind,
                At = DateTime.Now,
            });
            // 只留最近 100 条
            if (file.History.Count > 100)
                file.History.RemoveRange(100, file.History.Count - 100);
            Core.JsonStore.Write(HistoryPath, file);

            ShowBalloon(title, body, kind);
        }

        private static void ShowBalloon(string title, string body, string kind)
        {
            try
            {
                if (_icon == null)
                {
                    _icon = new NotifyIcon
                    {
                        Icon = Theme.AppIcon(),
                        Text = "灵枢 · SpiritScout",
                        Visible = true,
                    };
                }
                _icon.BalloonTipTitle = title ?? "灵枢";
                _icon.BalloonTipText = body ?? "";
                _icon.BalloonTipIcon = kind == "warn" ? ToolTipIcon.Warning
                    : (kind == "ok" ? ToolTipIcon.Info : ToolTipIcon.Info);
                _icon.ShowBalloonTip(6000);
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("弹通知失败: " + ex.Message);
            }
        }

        /// <summary>程序退出时把托盘图标清掉，否则会留一个幽灵图标。</summary>
        public static void Shutdown()
        {
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
            }
            catch { }
        }
    }

    public sealed class NotifyPage : IPage
    {
        public string Id { get { return "notify"; } }
        public string Title { get { return "通知"; } }
        public PageGroup Group { get { return PageGroup.System; } }

        private CheckBox _onComplete;
        private CheckBox _onFailure;
        private CheckBox _onQuestion;
        private CheckBox _sound;
        private DataTable _grid;
        private Label _hint;

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var test = Theme.Primary("发一条测试通知", 132);
            test.Click += (s, e) =>
            {
                NotifyCenter.Push("灵枢 · 测试通知",
                    "如果你看到这个气泡，说明桌面通知可用。", "ok");
                _hint.Text = "已发送（看右下角托盘区）";
                _hint.ForeColor = Theme.Ok;
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            };

            var clear = Theme.Ghost("清空历史", 92);
            clear.Click += (s, e) => ClearHistory();

            var head = Theme.PageHeader("通知",
                "长任务跑完时弹系统气泡，人可以去干别的", test, clear);

            // 开关分区块
            var card = new SectionCard("什么时候通知我", "系统气泡 · 右下角托盘区",
                Theme.Accent, "◔")
            {
                Dock = DockStyle.Top,
                Height = 204,
                Margin = new Padding(0, 0, 0, 14),
            };

            _onComplete = MakeCheck("任务完成时", 2, 6);
            _onFailure = MakeCheck("任务失败或被中止时", 2, 36);
            _onQuestion = MakeCheck("智能体需要我拍板时", 2, 66);
            _sound = MakeCheck("同时播放提示音", 2, 96);

            card.Body.Controls.Add(_onComplete);
            card.Body.Controls.Add(_onFailure);
            card.Body.Controls.Add(_onQuestion);
            card.Body.Controls.Add(_sound);

            _grid = new DataTable { Dock = DockStyle.Fill };
            _grid.AddColumn("time", "时间", 1.1f);
            _grid.AddColumn("title", "标题", 1.6f);
            _grid.AddColumn("body", "内容", 4f);


            _grid.EmptyText = "还没有通知记录。点「发一条测试通知」看看效果。";

            var gridCard = new SectionCard("通知历史", "最多保留最近 100 条",
                Theme.Ok, "◷")
            { Dock = DockStyle.Fill };
            gridCard.Body.Controls.Add(_grid);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            root.Controls.Add(gridCard);
            root.Controls.Add(card);
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }

        private CheckBox MakeCheck(string text, int x, int y)
        {
            var cb = new Widgets.Switch
            {
                Text = text,
                ForeColor = Theme.Text,
                Font = Theme.FontBody,
                AutoSize = true,
                Location = new Point(x, y),
            };
            cb.CheckedChanged += (s, e) =>
            {
                var cfg = NotifyCenter.Config;
                cfg.OnComplete = _onComplete.Checked;
                cfg.OnFailure = _onFailure.Checked;
                cfg.OnQuestion = _onQuestion.Checked;
                cfg.SoundEnabled = _sound.Checked;
                NotifyCenter.SaveConfig();
            };
            return cb;
        }

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;

            var cfg = NotifyCenter.Config;
            _onComplete.Checked = cfg.OnComplete;
            _onFailure.Checked = cfg.OnFailure;
            _onQuestion.Checked = cfg.OnQuestion;
            _sound.Checked = cfg.SoundEnabled;

            var file = Core.JsonStore.Read<NotifyFile>(NotifyCenter.HistoryPath, null);
            var rows = new List<Dictionary<string, object>>();
            if (file != null && file.History != null)
            {
                foreach (var e in file.History)
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        { "time", e.At.ToString("MM-dd HH:mm:ss") },
                        { "title", e.Title },
                        // 历史列表只做预览；完整正文本来就在通知气泡里
                        { "body", Core.Text.OneLine(e.Body, 200) },
                    });
                }
            }
            _grid.Fill(rows);
            _grid.Colorize("title", v =>
            {
                if (v.Contains("失败")) return (Color?)Theme.Danger;
                if (v.Contains("完成")) return (Color?)Theme.Ok;
                return null;
            });

            // 空态文案已经由表格自己占位显示，这里只报条数，别把同一句话写两遍
            _hint.Text = "共 " + rows.Count + " 条（最多保留 100 条）";
            await Task.CompletedTask;
        }

        private void ClearHistory()
        {
            if (!UiSafety.Confirm(FindShell(), "清空全部通知历史？")) return;
            Core.JsonStore.Write(NotifyCenter.HistoryPath, new NotifyFile());
            UiSafety.Fire(RefreshAsync(), "刷新列表");
        }

        private Form FindShell()
        {
            Control c = _grid;
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
