// 灵枢桌面版 —— 页面：设置 / 关于
//
// 设置页聚焦「用户真正会用到」的四件事：
//   · 看清楚数据存在哪（并且一键打开）
//   · 备份 / 换机迁移
//   · 清日志
//   · 默认参数
//
// 不做「切换主题」这类假开关——界面目前只有深色一套，写了开关也无效。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class SettingsPage : IPage
    {
        public string Id { get { return "settings-general"; } }
        public string Title { get { return "设置"; } }
        public PageGroup Group { get { return PageGroup.System; } }

        /// <summary>装载配置时置 true，避免把「初始化」当成「用户改动」写回去。</summary>
        private bool _loading;

        private ComboBox _defaultModel;
        private TextBox _recall;
        private CheckBox _autoDistill;
        private Label _pathLabel;
        private Label _sizeLabel;
        private Label _hint;

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
                AutoScroll = true,
            };

            // 标题先创建、最后加入（Dock.Top 后加的在上面），保证停在页面顶端。
            var header = Theme.PageHeader("设置", "数据都在本地，不上传");

            // ---- 数据位置 ----
            var card1 = new SectionCard("数据位置", "本机存储 · 一键打开 / 备份迁移",
                Theme.Accent, "◫")
            {
                Dock = DockStyle.Top,
                Height = 172,
                Margin = new Padding(0, 0, 0, 14),
            };

            _pathLabel = new Label
            {
                Text = "",
                ForeColor = Theme.Text,
                Font = Theme.FontMono,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Size = new Size(760, 22),
                Location = new Point(20, 4),
            };
            _sizeLabel = new Label
            {
                Text = "",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(20, 30),
            };
            card1.Body.Controls.Add(_pathLabel);
            card1.Body.Controls.Add(_sizeLabel);

            // 路径标签固定 760 宽，窄窗口下会戳出右缘（overflow_check 实测 +71px）。
            // 跟着容器宽度收，配 AutoEllipsis 省略号截尾。
            var body1 = card1.Body;
            body1.Resize += (s, e) =>
            {
                if (Theme.IsScaling) return;
                float u = Theme.UnitOf(body1);
                int w = body1.ClientSize.Width - (int)Math.Round(32f * u);   // 左 20 + 右 12（设计值）
                if (w > (int)Math.Round(120f * u)) _pathLabel.Width = w;
            };

            var openBtn = Theme.Ghost("打开文件夹", 110, 32);
            openBtn.Location = new Point(20, 58);
            openBtn.Click += (s, e) => OpenFolder();
            card1.Body.Controls.Add(openBtn);

            var backupBtn = Theme.Primary("备份数据…", 110, 32);
            backupBtn.Location = new Point(140, 58);
            backupBtn.Click += (s, e) => Backup();
            card1.Body.Controls.Add(backupBtn);

            // ---- 日志 ----
            var card2 = new SectionCard("运行日志", "出问题时把日志发出来，能定位大部分故障",
                Theme.Warn, "◷")
            {
                Dock = DockStyle.Top,
                Height = 144,
                Margin = new Padding(0, 0, 0, 14),
            };
            // 「查看日志」放在最左——它是第一反应要点的
            var viewLog = Theme.Primary("查看日志", 100, 32);
            viewLog.Location = new Point(20, 6);
            viewLog.Click += (s, e) =>
            {
                using (var dlg = new LogViewer())
                {
                    dlg.ShowDialog(ShellOf());
                }
            };
            card2.Body.Controls.Add(viewLog);

            var openLog = Theme.Ghost("打开日志目录", 122, 32);
            openLog.Location = new Point(130, 6);
            openLog.Click += (s, e) => OpenPath(Core.AppPaths.LogDir);
            card2.Body.Controls.Add(openLog);

            var clearLog = Theme.DangerBtn("清空日志", 100, 32);
            clearLog.Location = new Point(262, 6);
            clearLog.Click += (s, e) => ClearLogs();
            card2.Body.Controls.Add(clearLog);

            // ---- 运行偏好（这几项是真的会被读取的） ----
            var card3 = new SectionCard("运行偏好", "默认模型 · 经验召回 · 自动提炼",
                Theme.Violet, "✦")
            {
                Dock = DockStyle.Top,
                Height = 190,
            };

            card3.Body.Controls.Add(new Label
            {
                Text = "默认模型",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(20, 8),
            });
            _defaultModel = Theme.Select();
            _defaultModel.Location = new Point(114, 4);
            _defaultModel.Width = 260;
            _defaultModel.SelectedIndexChanged += (s3, e3) => SavePrefs();
            card3.Body.Controls.Add(_defaultModel);
            card3.Body.Controls.Add(new Label
            {
                Text = "新建会话时默认用它",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(390, 9),
            });

            card3.Body.Controls.Add(new Label
            {
                Text = "经验召回",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(20, 48),
            });
            _recall = Theme.Input();
            _recall.Location = new Point(114, 44);
            _recall.Width = 60;
            _recall.TextChanged += (s3, e3) => SavePrefs();
            card3.Body.Controls.Add(_recall);
            card3.Body.Controls.Add(new Label
            {
                Text = "每轮带几条经验（0 = 关）",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(188, 49),
            });

            _autoDistill = new Widgets.Switch
            {
                Text = "任务完成后自动提炼经验",
                ForeColor = Theme.Text,
                Font = Theme.FontBody,
                AutoSize = true,
                Location = new Point(20, 82),
            };
            _autoDistill.CheckedChanged += (s3, e3) => SavePrefs();
            card3.Body.Controls.Add(_autoDistill);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            root.Controls.Add(card3);
            root.Controls.Add(card2);
            root.Controls.Add(card1);
            root.Controls.Add(header);   // 最后加 → 顶端
            root.Controls.Add(_hint);
            return root;
        }

        public async Task RefreshAsync()
        {
            if (_pathLabel == null || _pathLabel.IsDisposed) return;
            _pathLabel.Text = Core.AppPaths.DataRoot;
            LoadPrefs();
            try
            {
                long size = 0;
                int files = 0;
                foreach (var f in Directory.GetFiles(Core.AppPaths.DataRoot, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(f).Length; files++; } catch { }
                }
                string sizeText = string.Format("占用 {0} · {1} 个文件 · 位置类型：{2}",
                    Core.Text.Size(size), files, Core.AppPaths.RootKind);

                // 数据来自更新的程序时提示一句。
                // 不弹窗（升级/降级是常事，每次启动弹框太烦），
                // 但摆在数据位置旁边——真要出问题，用户能在这里看到线索。
                int dataVer = 0;
                try
                {
                    var probe = new Core.ConfigManager();
                    probe.Load();
                    dataVer = probe.Current.SchemaVersion;
                }
                catch { }
                if (Core.DataVersion.IsFromNewer("config", dataVer))
                {
                    sizeText += "  ·  ⚠ 配置来自更新的程序（v" + dataVer + "）";
                    _sizeLabel.ForeColor = Theme.Warn;
                }

                // 会话文件同理。**这段之前漏了**：只查了 config，
                // 某个会话来自更新版本时不提示。
                // 不用额外扫盘——List() 已经把每个会话的元信息读进内存
                // （带 1 秒缓存），直接取最大值即可。
                int sessionVer = 0;
                try
                {
                    foreach (var one in new Core.SessionStore().List())
                    {
                        if (one.SchemaVersion > sessionVer) sessionVer = one.SchemaVersion;
                    }
                }
                catch { }
                if (Core.DataVersion.IsFromNewer("session", sessionVer))
                {
                    sizeText += "  ·  ⚠ 有会话来自更新的程序（v" + sessionVer + "）";
                    _sizeLabel.ForeColor = Theme.Warn;
                }

                _sizeLabel.Text = sizeText;
            }
            catch (Exception ex)
            {
                _sizeLabel.Text = "读取失败：" + ex.Message;
            }
            await Task.CompletedTask;
        }

        /// <summary>把配置读进控件。</summary>
        private void LoadPrefs()
        {
            if (_defaultModel == null || _defaultModel.IsDisposed) return;
            _loading = true;
            try
            {
                var cfg = new Core.ConfigManager();
                cfg.Load();
                var c = cfg.Current;

                // 模型下拉
                var names = new List<string>();
                names.Add("（用第一个启用的模型）");
                try
                {
                    var reg = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                        new Llm.SecretStore(Core.AppPaths.SecretsFile));
                    foreach (var m in reg.Models())
                    {
                        if (m.Enabled) names.Add(m.Name);
                    }
                    reg.Dispose();
                }
                catch (Exception ex)
                {
                    // 模型列表加载不出来时下拉框会是空的，用户会以为「一个模型都没配」。
                    // 至少留个日志，排障时能看到原因。
                    Core.ConfigManager.Log("读取模型列表失败：" + ex.GetType().Name + ": " + ex.Message);
                }

                _defaultModel.Items.Clear();
                foreach (var n in names) _defaultModel.Items.Add(n);
                int idx = 0;
                if (!string.IsNullOrEmpty(c.DefaultModel))
                {
                    int found = names.IndexOf(c.DefaultModel);
                    if (found > 0) idx = found;
                }
                _defaultModel.SelectedIndex = idx;

                _recall.Text = (c.RecallLimit <= 0 ? 0 : c.RecallLimit).ToString();
                _autoDistill.Checked = c.DistillEnabled;
            }
            catch (Exception ex)
            {
                _hint.Text = "读取偏好失败：" + Theme.OneLine(ex.Message, 80);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>把控件写回配置。</summary>
        private void SavePrefs()
        {
            if (_loading || _defaultModel == null || _defaultModel.IsDisposed) return;
            try
            {
                var cfg = new Core.ConfigManager();
                cfg.Load();
                var c = cfg.Current;

                int sel = _defaultModel.SelectedIndex;
                c.DefaultModel = sel <= 0 ? "" : (_defaultModel.SelectedItem ?? "").ToString();

                int n;
                c.RecallLimit = int.TryParse((_recall.Text ?? "").Trim(), out n)
                    ? Math.Max(0, Math.Min(20, n)) : 6;

                c.DistillEnabled = _autoDistill.Checked;
                cfg.Save();
            }
            catch (Exception ex)
            {
                _hint.Text = "保存偏好失败：" + Theme.OneLine(ex.Message, 80);
                _hint.ForeColor = Theme.Danger;
            }
        }

        private Form ShellOf()
        {
            Control c = _pathLabel;
            while (c != null)
            {
                var f = c as Form;
                if (f != null) return f;
                c = c.Parent;
            }
            return null;
        }

        private void OpenFolder() { OpenPath(Core.AppPaths.DataRoot); }

        // 非 static：里面要用 ShellOf() 给消息框找 owner（两个调用点都是实例上下文）
        private void OpenPath(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                // 补上 owner：这是在按钮回调里、宿主窗口存在。
                // 不传 owner 的消息框可能跑到主窗口**后面**（尤其主窗口置顶/多显示器时），
                // 用户会以为"点了没反应"。
                MessageBox.Show(ShellOf(), "打开失败：" + ex.Message, "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Backup()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择把数据备份到哪里（会创建一个带时间戳的子目录）";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    string target = Path.Combine(dlg.SelectedPath,
                        "SpiritScout-备份-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    CopyDir(Core.AppPaths.DataRoot, target);
                    _hint.Text = "已备份到 " + target;
                    _hint.ForeColor = Theme.Ok;
                    Process.Start("explorer.exe", "\"" + dlg.SelectedPath + "\"");
                }
                catch (Exception ex)
                {
                    _hint.Text = "备份失败：" + Theme.OneLine(ex.Message, 100);
                    _hint.ForeColor = Theme.Danger;
                }
            }
        }

        /// <summary>
        /// 递归拷贝，但**跳过没有恢复价值的东西**。
        ///
        /// 实测：一开始是无条件全拷，结果备份里混进三类垃圾——
        ///   · `.tmp`：写到一半的残留，拷过去还可能被当成有效文件
        ///   · `logs/`：日志单份上限 8MB、留 5 份，白拷 40MB
        ///   · `.broken-*`：这个**保留**——它是配置损坏时的原件，体积小、有诊断价值
        /// </summary>
        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);

            // 日志目录整个跳过
            string logDir = Core.AppPaths.LogDir;
            if (string.Equals(from, logDir, StringComparison.OrdinalIgnoreCase)) return;

            foreach (var f in Directory.GetFiles(from))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.Copy(f, Path.Combine(to, name), true);
                }
                catch (IOException)
                {
                    // 单个文件被占用（比如正在写的日志）不该让整个备份失败
                }
            }
            foreach (var d in Directory.GetDirectories(from))
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        private void ClearLogs()
        {
            if (!UiSafety.Confirm(ShellOf(), "确定清空所有运行日志？\n\n不影响会话与配置。")) return;
            try
            {
                int n = 0;
                foreach (var f in Directory.GetFiles(Core.AppPaths.LogDir))
                {
                    try { File.Delete(f); n++; } catch { }
                }
                _hint.Text = "已清空 " + n + " 个日志文件";
                _hint.ForeColor = Theme.Ok;
            }
            catch (Exception ex)
            {
                _hint.Text = "清空失败：" + Theme.OneLine(ex.Message, 100);
                _hint.ForeColor = Theme.Danger;
            }
        }

    }

    // ===================== 关于 =====================

    public sealed class AboutPage : IPage
    {
        public string Id { get { return "about"; } }
        public string Title { get { return "关于"; } }
        public PageGroup Group { get { return PageGroup.System; } }

        private TextBox _checkOutput;

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var runCheck = Theme.Primary("运行自检", 96, 32);
            runCheck.Click += (s2, e2) => UiSafety.Fire(RunSelfCheckAsync(), "运行自检");

            var checkNet = Theme.Ghost("含网络测试", 104, 32);
            checkNet.Click += (s2, e2) => UiSafety.Fire(RunSelfCheckAsync(true), "自检（含网络）");

            var copy = Theme.Ghost("复制报告", 92, 32);
            copy.Click += (s2, e2) => CopyReport();

            // web 的 view-about 标题是产品名不是「关于」（和页面 Id 分开）。
            // 下面那张大卡就是产品信息，页头写「关于」反而重复。
            root.Controls.Add(Theme.PageHeader("灵枢 · SpiritScout",
                "渗透测试智能体集群 · 本地运行 · 单文件零依赖",
                runCheck, checkNet, copy));

            // 版本号唯一来源是 Core\AppInfo.cs；
            // 原来用 Assembly.GetName().Version 会显示成 "2.0.0.0"（四段），
            // 和别处的 "2.0.0" 对不上。
            string version = Core.AppInfo.Version;
            var card = new SectionCard("灵枢 · SpiritScout",
                "版本 " + version + " · 本地运行 · 单文件零依赖",
                Theme.Accent, "✦")
            {
                Dock = DockStyle.Top,
                Height = 460,
                Margin = new Padding(0, 0, 0, 14),
            };

            var text = new Label
            {
                Text =
                    "一个本地运行的安全测试 / CTF 解题助手。\n"
                    + "全部数据保存在本机，不联网上传；模型调用只发往你自己配置的端点。\n\n"
                    + "技术栈\n"
                    + "· 纯 C# + WinForms，运行在 Windows 自带的 .NET Framework 4.8 上\n"
                    + "· 不依赖浏览器内核、不依赖 Python、不监听任何端口\n"
                    + "· 数据格式：JSON（可直接打开查看）\n\n"
                    + "数据目录\n"
                    + Core.AppPaths.DataRoot + "\n\n"
                    + "界面字体：" + Theme.UiFontName
                    + " / 等宽：" + Theme.MonoFontName + "\n\n"
                    + "升级方式\n"
                    + "下载新的 exe 覆盖旧的即可，数据目录不会被触碰。",
                ForeColor = Theme.Text,
                Font = Theme.FontBody,
                AutoSize = false,
                BackColor = Color.Transparent,
                // 宽度别写死：1024 宽的窗口减掉侧栏和 padding 只剩约 756px，
                // 原来固定 820 会溢出 41px（溢出检查抓到的）。
                Dock = DockStyle.Fill,
            };
            card.Body.Controls.Add(text);

            _checkOutput = new TextBox
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
                Text = "点右上角「运行自检」检查环境。\r\n\r\n"
                     + "它会验证：数据目录可写、配置文件完整、模型可用、"
                     + "技能库加载、内置工具生成、经验库可读、日志目录可写。\r\n"
                     + "点「含网络测试」会额外真的向模型端点发一次请求（约 5 秒）。",
            };

            var checkCard = new SectionCard("环境自检", "离线检查 + 可选网络测试",
                Theme.Ok, "✓")
            { Dock = DockStyle.Fill };
            checkCard.Body.Controls.Add(_checkOutput);

            root.Controls.Add(checkCard);
            root.Controls.Add(card);
            return root;
        }

        private async Task RunSelfCheckAsync(bool withNetwork = false)
        {
            if (_checkOutput == null || _checkOutput.IsDisposed) return;
            _checkOutput.Text = "正在检查…\r\n";

            var items = SelfCheck.RunOffline();
            if (withNetwork)
            {
                _checkOutput.Text += "正在测试模型端点（会真的发请求）…\r\n";
                items.Add(await SelfCheck.CheckEndpointAsync("").ConfigureAwait(true));
            }

            _checkOutput.Text = SelfCheck.ToText(items, Core.AppInfo.Version);
            _checkOutput.SelectionStart = 0;
            _checkOutput.ScrollToCaret();
        }

        private void CopyReport()
        {
            if (_checkOutput == null || string.IsNullOrWhiteSpace(_checkOutput.Text)) return;
            try
            {
                Clipboard.SetText(_checkOutput.Text);
                _checkOutput.Text += "\r\n\r\n（报告已复制到剪贴板）";
            }
            catch (Exception ex)
            {
                // 剪贴板被别的程序占着时会失败。静默的话用户以为复制成功了，
                // 粘贴出去是空的才发现。
                _checkOutput.Text += "\r\n\r\n（复制失败：" + Theme.OneLine(ex.Message, 60) + "）";
                Core.ConfigManager.Log("复制自检报告失败：" + ex.Message);
            }
        }

        public Task RefreshAsync() { return Task.CompletedTask; }
    }
}
