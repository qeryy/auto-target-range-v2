// 灵枢桌面版（原生）—— 主窗口外壳
//
// 职责收窄成三件：
//   1. 画左侧导航（**从 PageRegistry 自动生成**，不硬编码）；
//   2. 在右侧容器里切换页面（页面自己管内容）；
//   3. 底部状态栏（提示、连接状态）。
//
// 加一个页面只要写个 IPage 并在 Program.RegisterPages 里注册一行，这里不用动。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Native
{
    public sealed class MainForm : Form
    {
        private readonly BackendService _service;
        private readonly AppContext _ctx;

        private Panel _navPanel;
        private Panel _contentHost;
        private Panel _startupPanel;
        private Label _statusLabel;
        private Label _startupHint;
        private TextBox _logBox;

        private readonly Dictionary<string, Button> _navButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Control> _viewCache = new Dictionary<string, Control>();
        private IPage _current;

        public MainForm(BackendService service, SpiritApi api)
        {
            _service = service;
            _ctx = new AppContext(api, this);

            Text = "灵枢 · SpiritScout";
            ClientSize = new Size(1400, 900);
            MinimumSize = new Size(1180, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = UiKit.Bg;
            ForeColor = UiKit.Text;
            Font = UiKit.FontBody;
            Icon = LoadAppIcon();
            DoubleBuffered = true;

            _ctx.Navigate = Go;
            _ctx.Toast = ShowToast;

            BuildShell();
            ShowStartupOverlay();
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                var asm = typeof(MainForm).Assembly;
                using (var stream = asm.GetManifestResourceStream("icon.ico"))
                {
                    if (stream != null) return new Icon(stream);
                }
            }
            catch { }
            return null;
        }

        // ---------- 外壳 ----------

        private void BuildShell()
        {
            // 先建内容区（Dock.Fill 要在导航之后加才会填满剩余空间）
            _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = UiKit.Bg };

            _navPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 216,
                BackColor = UiKit.Surface,
                Padding = new Padding(0),
            };

            // 品牌区
            var brand = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = UiKit.Surface };
            var brandTitle = new Label
            {
                Text = "灵枢",
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
                ForeColor = UiKit.Text,
                AutoSize = true,
                Location = new Point(20, 12),
            };
            var brandSub = new Label
            {
                Text = "SpiritScout",
                Font = new Font("Microsoft YaHei UI", 8F),
                ForeColor = UiKit.Muted,
                AutoSize = true,
                Location = new Point(22, 36),
            };
            brand.Controls.Add(brandTitle);
            brand.Controls.Add(brandSub);

            // 状态栏
            _statusLabel = new Label
            {
                Text = "正在启动…",
                Dock = DockStyle.Bottom,
                Height = 44,
                ForeColor = UiKit.Muted,
                Font = UiKit.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(20, 0, 8, 0),
            };

            _navPanel.Controls.Add(brand);
            _navPanel.Controls.Add(_statusLabel);

            // 导航：按分组从注册表生成（倒序加入，Dock.Top 后加的在上）
            var groups = new[]
            {
                PageGroup.System, PageGroup.Resource, PageGroup.Main,
            };
            foreach (var g in groups)
            {
                var pages = PageRegistry.ByGroup(g);
                if (pages.Count == 0) continue;

                var list = new List<IPage>(pages);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var page = list[i];
                    var btn = new Button
                    {
                        Text = "  " + page.Title,
                        Tag = page.Id,
                        Dock = DockStyle.Top,
                        Height = 38,
                        FlatStyle = FlatStyle.Flat,
                        TextAlign = ContentAlignment.MiddleLeft,
                        ForeColor = UiKit.Muted,
                        BackColor = UiKit.Surface,
                        Cursor = Cursors.Hand,
                        TabStop = false,
                        Font = UiKit.FontBody,
                    };
                    btn.FlatAppearance.BorderSize = 0;
                    btn.FlatAppearance.MouseOverBackColor = UiKit.SurfaceHover;
                    string id = page.Id;
                    btn.Click += async (s, e) => await GoAsync(id).ConfigureAwait(true);
                    _navButtons[id] = btn;
                    _navPanel.Controls.Add(btn);
                }

                // 分组之间的细分隔线
                var sep = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 1,
                    BackColor = UiKit.Line,
                    Margin = new Padding(0, 6, 0, 6),
                };
                _navPanel.Controls.Add(sep);
            }

            Controls.Add(_contentHost);
            Controls.Add(_navPanel);
        }

        // ---------- 切页 ----------

        private async Task GoAsync(string pageId)
        {
            var page = PageRegistry.Find(pageId);
            if (page == null) return;
            if (_current != null && _current.Id == pageId)
            {
                await page.RefreshAsync().ConfigureAwait(true);
                return;
            }

            Control view;
            if (!_viewCache.TryGetValue(pageId, out view))
            {
                view = page.CreateView();
                view.Dock = DockStyle.Fill;
                _viewCache[pageId] = view;
            }

            _contentHost.SuspendLayout();
            _contentHost.Controls.Clear();
            _contentHost.Controls.Add(view);
            _contentHost.ResumeLayout();

            _current = page;
            foreach (var kv in _navButtons)
            {
                bool on = kv.Key == pageId;
                kv.Value.ForeColor = on ? UiKit.Accent : UiKit.Muted;
                kv.Value.BackColor = on ? UiKit.SurfaceHover : UiKit.Surface;
            }

            try
            {
                await page.RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _ctx.ShowError(page.Title + "加载", ex);
            }
        }

        private void Go(string pageId)
        {
            GoAsync(pageId).GetAwaiter();
        }

        // ---------- 状态栏提示 ----------

        private void ShowToast(string message, bool ok)
        {
            if (_statusLabel == null || _statusLabel.IsDisposed) return;
            _statusLabel.Text = message;
            _statusLabel.ForeColor = ok ? UiKit.Ok : UiKit.Danger;
            var timer = new Timer { Interval = 6000 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                if (_statusLabel != null && !_statusLabel.IsDisposed)
                {
                    _statusLabel.Text = _connectedText;
                    _statusLabel.ForeColor = UiKit.Muted;
                }
            };
            timer.Start();
        }

        private string _connectedText = "未连接";

        // ---------- 启动遮罩 ----------

        private void ShowStartupOverlay()
        {
            _startupPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiKit.Bg };

            var title = new Label
            {
                Text = "灵枢 · SpiritScout",
                Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold),
                ForeColor = UiKit.Text,
                AutoSize = true,
                Location = new Point(48, 44),
            };
            _startupHint = new Label
            {
                Text = "正在启动后端服务…",
                Font = new Font("Microsoft YaHei UI", 10F),
                ForeColor = UiKit.Accent,
                AutoSize = true,
                Location = new Point(50, 96),
            };
            _logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(9, 12, 16),
                ForeColor = UiKit.Muted,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiKit.FontMono,
                Location = new Point(48, 132),
                Size = new Size(ClientSize.Width - 96, ClientSize.Height - 210),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };

            _startupPanel.Controls.Add(title);
            _startupPanel.Controls.Add(_startupHint);
            _startupPanel.Controls.Add(_logBox);
            _contentHost.Controls.Clear();
            _contentHost.Controls.Add(_startupPanel);

            _service.Log += line =>
            {
                if (_logBox == null || _logBox.IsDisposed) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        try { _logBox.AppendText(line + Environment.NewLine); } catch { }
                    }));
                }
                catch { }
            };
        }

        private void HideStartupOverlay()
        {
            if (_startupPanel == null) return;
            _contentHost.Controls.Remove(_startupPanel);
            _startupPanel.Dispose();
            _startupPanel = null;
            _logBox = null;
        }

        // ---------- 启动 ----------

        public async Task BootAsync(string baseDir)
        {
            try
            {
                await Task.Run(() => _service.Start(baseDir)).ConfigureAwait(true);
                HideStartupOverlay();
                _connectedText = "已连接 · 管道通信（未监听端口）";
                _statusLabel.Text = _connectedText;
                _statusLabel.ForeColor = UiKit.Ok;

                // 首个页面
                var first = PageRegistry.All.Count > 0 ? PageRegistry.All[0] : null;
                if (first != null) await GoAsync(first.Id).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (_startupHint != null && !_startupHint.IsDisposed)
                {
                    _startupHint.Text = "启动失败";
                    _startupHint.ForeColor = UiKit.Danger;
                }
                if (_logBox != null && !_logBox.IsDisposed)
                {
                    _logBox.AppendText(Environment.NewLine + "=== 失败 ===" + Environment.NewLine
                        + ex.Message + Environment.NewLine);
                }
                _statusLabel.Text = "未连接";
                _statusLabel.ForeColor = UiKit.Danger;
                MessageBox.Show(this, ex.Message, "后端启动失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>后端就绪、管道接上之后，刷新当前页。</summary>
        public async Task OnBackendReadyAsync()
        {
            if (_current != null)
            {
                try { await _current.RefreshAsync().ConfigureAwait(true); }
                catch (Exception ex) { _ctx.ShowError(_current.Title, ex); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try { _service.Stop(); } catch { }
        }
    }
}
