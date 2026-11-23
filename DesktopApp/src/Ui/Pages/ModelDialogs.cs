// 灵枢桌面版 —— 供应商 / 模型 编辑对话框
//
// 两个对话框放一起：字段少、风格一致、改起来一处改。

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Llm;

namespace SpiritScout.Ui.Pages
{
    /// <summary>新增 / 编辑供应商。</summary>
    public sealed class ProviderDialog : Form
    {
        private readonly ProviderSpec _spec;
        private readonly ModelRegistry _registry;
        private readonly bool _isNew;

        private TextBox _name;
        private ComboBox _protocol;
        private TextBox _baseUrl;
        private TextBox _apiKey;
        private Label _keyState;
        private Label _error;

        public ProviderDialog(ProviderSpec spec, ModelRegistry registry)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            _registry = registry;
            _isNew = spec == null;
            _spec = spec ?? new ProviderSpec();

            Text = _isNew ? "添加供应商" : "编辑供应商";
            ClientSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;

            Build();
        }

        private sealed class ProviderPreset
        {
            public string Name;
            public string Url;
            public bool Anthropic;
            public ProviderPreset(string n, string u, bool a)
            { Name = n; Url = u; Anthropic = a; }
        }

        private static readonly ProviderPreset[] Presets = new[]
        {
            new ProviderPreset("deepseek",    "https://api.deepseek.com/v1", false),
            new ProviderPreset("openai",      "https://api.openai.com/v1", false),
            new ProviderPreset("moonshot",    "https://api.moonshot.cn/v1", false),
            new ProviderPreset("zhipu",       "https://open.bigmodel.cn/api/paas/v4", false),
            new ProviderPreset("dashscope",   "https://dashscope.aliyuncs.com/compatible-mode/v1", false),
            new ProviderPreset("siliconflow", "https://api.siliconflow.cn/v1", false),
            new ProviderPreset("anthropic",   "https://api.anthropic.com", true),
            new ProviderPreset("lmstudio",    "http://127.0.0.1:1234/v1", false),
            new ProviderPreset("ollama",      "http://127.0.0.1:11434/v1", false),
        };

        private static string[] PresetNames()
        {
            var list = new System.Collections.Generic.List<string>();
            list.Add("（自己填）");
            foreach (var p in Presets) list.Add(p.Name);
            return list.ToArray();
        }

        private void Build()
        {
            int x = 24, y = 22, fx = 112;
            int fw = ClientSize.Width - fx - 32;

            // ---- 快速填充：新用户不知道端点地址填什么，这里给几个常用的 ----
            Row("快速填充", x, y);
            var preset = Theme.Select(PresetNames());
            preset.Location = new Point(fx, y);
            preset.Width = 300;
            preset.SelectedIndex = 0;
            preset.SelectedIndexChanged += (s2, e2) =>
            {
                int i = preset.SelectedIndex - 1;   // 0 是「（自己填）」
                if (i < 0 || i >= Presets.Length) return;
                var pr = Presets[i];
                _name.Text = pr.Name;
                _baseUrl.Text = pr.Url;
                _protocol.SelectedIndex = pr.Anthropic ? 1 : 0;
            };
            Controls.Add(preset);
            Controls.Add(new Label
            {
                Text = "选一个自动填好下面几项，再贴 API Key 即可",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = true,
                Location = new Point(fx + 310, y + 4),
            });
            y += 40;

            Row("名称 *", x, y);
            _name = Theme.Input();
            _name.Text = _spec.Name;
            _name.Location = new Point(fx, y);
            _name.Width = fw;
            Controls.Add(_name);
            y += 40;

            Row("协议 *", x, y);
            _protocol = Theme.Select("OpenAI 兼容（多数端点）", "Anthropic（Claude）");
            _protocol.SelectedIndex = _spec.Protocol == ApiProtocol.AnthropicCompatible ? 1 : 0;
            _protocol.Location = new Point(fx, y);
            _protocol.Width = 300;
            Controls.Add(_protocol);
            y += 40;

            Row("端点地址 *", x, y);
            _baseUrl = Theme.Input();
            _baseUrl.Text = _spec.BaseUrl;
            _baseUrl.Location = new Point(fx, y);
            _baseUrl.Width = fw;
            Controls.Add(_baseUrl);
            Controls.Add(new Label
            {
                Text = "例：https://api.deepseek.com/v1　或　http://127.0.0.1:1234/v1",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = true,
                Location = new Point(fx, y + 26),
            });
            y += 58;

            Row("API Key", x, y);
            _apiKey = Theme.Input();
            _apiKey.UseSystemPasswordChar = true;
            _apiKey.Location = new Point(fx, y);
            _apiKey.Width = fw - 90;
            Controls.Add(_apiKey);

            _keyState = new Label
            {
                Location = new Point(fx + fw - 82, y + 4),
                AutoSize = true,
                Font = Theme.FontSmall,
            };
            Controls.Add(_keyState);
            UpdateKeyState();
            y += 40;

            Controls.Add(new Label
            {
                Text = "密钥用 Windows DPAPI 加密后存本机，不上传、不写明文。\n"
                     + "留空表示【保留原有密钥】；输入新值则覆盖。",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = false,
                Size = new Size(fw, 46),
                Location = new Point(fx, y - 4),
            });
            y += 46;

            _error = new Label
            {
                Location = new Point(x, y),
                Size = new Size(ClientSize.Width - 48, 40),
                ForeColor = Theme.Danger,
                Font = Theme.FontSmall,
            };
            Controls.Add(_error);

            var ok = Theme.Primary("保存", 104, 36);
            ok.Location = new Point(ClientSize.Width - 136, ClientSize.Height - 60);
            ok.Click += (s, e) => Submit();
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 36);
            cancel.Location = new Point(ClientSize.Width - 232, ClientSize.Height - 60);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void UpdateKeyState()
        {
            if (_isNew)
            {
                _keyState.Text = "新建";
                _keyState.ForeColor = Theme.Muted;
                return;
            }
            bool has = _registry.HasKey(_spec.Name);
            _keyState.Text = has ? "已配置" : "未配置";
            _keyState.ForeColor = has ? Theme.Ok : Theme.Warn;
        }

        private void Row(string text, int x, int y)
        {
            Controls.Add(Theme.FormLabel(text, x, y));
        }

        private void Submit()
        {
            _error.Text = "";
            string name = (_name.Text ?? "").Trim();
            string url = (_baseUrl.Text ?? "").Trim();

            if (name.Length == 0) { _error.Text = "名称不能为空。"; _name.Focus(); return; }
            if (url.Length == 0) { _error.Text = "端点地址不能为空。"; _baseUrl.Focus(); return; }
            if (_isNew && _registry.FindProvider(name) != null)
            {
                _error.Text = "已经有同名供应商了。";
                _name.Focus();
                return;
            }

            // 改名时把旧记录删掉（连带旧密钥），避免留下一个空壳
            if (!_isNew && !string.Equals(name, _spec.Name, StringComparison.OrdinalIgnoreCase))
                _registry.DeleteProvider(_spec.Name);

            var spec = new ProviderSpec
            {
                Name = name,
                Protocol = _protocol.SelectedIndex == 1
                    ? ApiProtocol.AnthropicCompatible
                    : ApiProtocol.OpenAiCompatible,
                BaseUrl = url,
            };
            _registry.UpsertProvider(spec);

            string key = (_apiKey.Text ?? "").Trim();
            if (key.Length > 0) _registry.SetKey(name, key);

            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>新增 / 编辑模型。</summary>
    public sealed class ModelDialog : Form
    {
        private readonly ModelSpec _spec;
        private readonly ModelRegistry _registry;
        private readonly bool _isNew;

        private TextBox _name;
        private ComboBox _provider;
        private ComboBox _modelId;
        private Button _fetch;
        private Label _fetchHint;
        private TextBox _ctx;
        private TextBox _price;
        private CheckBox _enabled;
        private Label _error;

        public ModelDialog(ModelSpec spec, ModelRegistry registry)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            _registry = registry;
            _isNew = spec == null;
            _spec = spec ?? new ModelSpec();

            Text = _isNew ? "添加模型" : "编辑模型";
            ClientSize = new Size(660, 404);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;

            Build();
        }

        /// <summary>常见模型 ID（只是候选，允许手填）。</summary>
        private static string[] CommonModelIds()
        {
            return new[]
            {
                "deepseek-chat",
                "deepseek-reasoner",
                "cn:deepseek-v4.1-flash",
                "gpt-4o",
                "gpt-4o-mini",
                "claude-sonnet-4-20250514",
                "moonshot-v1-8k",
                "glm-4-plus",
                "qwen-max",
                "qwen2.5-72b-instruct",
            };
        }

        private void Build()
        {
            int x = 24, y = 22, fx = 112;
            int fw = ClientSize.Width - fx - 32;

            Row("显示名 *", x, y);
            _name = Theme.Input();
            _name.Text = _spec.Name;
            _name.Location = new Point(fx, y);
            _name.Width = fw;
            Controls.Add(_name);
            y += 40;

            Row("供应商 *", x, y);
            var providers = _registry.Providers();
            var names = new string[providers.Count];
            for (int i = 0; i < providers.Count; i++) names[i] = providers[i].Name;
            _provider = Theme.Select(names);
            if (!_isNew)
            {
                int idx = Array.FindIndex(names,
                    n => string.Equals(n, _spec.ProviderName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _provider.SelectedIndex = idx;
            }
            _provider.Location = new Point(fx, y);
            _provider.Width = 300;
            Controls.Add(_provider);
            y += 40;

            Row("模型 ID *", x, y);
            // 可编辑下拉：给常见名字做候选，也能手填任意值
            _modelId = Theme.Combo(true);
            _modelId.Location = new Point(fx, y);
            _modelId.Width = fw - 104;
            foreach (var id in CommonModelIds()) _modelId.Items.Add(id);
            _modelId.Text = _spec.ModelId;
            Controls.Add(_modelId);

            // 本地的模型名是文件名或自定义名，猜不出来——直接问端点要
            _fetch = Theme.Ghost("从端点拉取", 96, 28);
            _fetch.Location = new Point(fx + fw - 100, y + 2);
            _fetch.Click += async (s2, e2) => await FetchModelsAsync().ConfigureAwait(true);
            Controls.Add(_fetch);

            // 说明文字挪到「从端点拉取」按钮下方：原来 y+26 的框顶比按钮底（y+30）还高，
            // 字号放大后两个框压在一起（压字探针抓到）。
            _fetchHint = new Label
            {
                Text = "端点上真实的模型名。下拉里是常见的；本地模型点右边「从端点拉取」",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = false,
                Size = new Size(fw, 20),
                Location = new Point(fx, y + 34),
            };
            Controls.Add(_fetchHint);
            y += 56;

            Row("单价(元/M)", x, y);
            _price = Theme.Input();
            _price.Text = _spec.PricePerMillionCny > 0
                ? _spec.PricePerMillionCny.ToString("G") : "";
            _price.Location = new Point(fx, y);
            _price.Width = 120;
            Controls.Add(_price);
            Controls.Add(new Label
            {
                Text = "每百万 token 的参考价，用来估算花费；留空或 0 = 不估算",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = true,
                Location = new Point(fx + 130, y + 4),
            });
            y += 44;

            Row("上下文", x, y);
            _ctx = Theme.Input();
            _ctx.Text = _spec.ContextWindow.ToString();
            _ctx.Location = new Point(fx, y);
            _ctx.Width = 140;
            Controls.Add(_ctx);
            y += 40;

            _enabled = new Widgets.Switch
            {
                Text = "启用（停用后不参与编排）",
                Checked = _spec.Enabled,
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(fx, y),
            };
            Controls.Add(_enabled);
            y += 36;

            _error = new Label
            {
                Location = new Point(x, y),
                Size = new Size(ClientSize.Width - 48, 30),
                ForeColor = Theme.Danger,
                Font = Theme.FontSmall,
            };
            Controls.Add(_error);

            var ok = Theme.Primary("保存", 104, 36);
            ok.Location = new Point(ClientSize.Width - 136, ClientSize.Height - 60);
            ok.Click += (s, e) => Submit();
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 36);
            cancel.Location = new Point(ClientSize.Width - 232, ClientSize.Height - 60);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Row(string text, int x, int y)
        {
            Controls.Add(Theme.FormLabel(text, x, y));
        }

        /// <summary>向当前供应商的端点要模型列表。</summary>
        private async Task FetchModelsAsync()
        {
            if (_fetch == null || _fetch.IsDisposed) return;

            string provName = _provider.SelectedItem == null ? "" : _provider.SelectedItem.ToString();
            if (provName.Length == 0)
            {
                _fetchHint.Text = "先选一个供应商";
                _fetchHint.ForeColor = Theme.Danger;
                return;
            }

            _fetch.Enabled = false;
            _fetchHint.Text = "正在向 " + provName + " 要模型列表…";
            _fetchHint.ForeColor = Theme.Accent;

            try
            {
                var names = await _registry.FetchModelsAsync(provName).ConfigureAwait(true);
                if (names.Count == 0)
                {
                    _fetchHint.Text = "端点没返回模型列表（有的服务不支持 /models）；可以照旧手填";
                    _fetchHint.ForeColor = Theme.Warn;
                    return;
                }

                // 保留手填的内容，只把拉到的新名字补进去
                string keep = _modelId.Text;
                _modelId.Items.Clear();
                foreach (var n in names) _modelId.Items.Add(n);
                _modelId.Text = keep;
                _modelId.DropDownStyle = ComboBoxStyle.DropDown;

                _fetchHint.Text = "拉到 " + names.Count + " 个模型，点下拉选一个（也可以继续手填）";
                _fetchHint.ForeColor = Theme.Ok;

                // 空着的话直接选第一个，省一次点击
                if (string.IsNullOrWhiteSpace(keep) && names.Count > 0)
                    _modelId.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                _fetchHint.Text = "拉取失败：" + Theme.OneLine(ex.Message, 60);
                _fetchHint.ForeColor = Theme.Danger;
            }
            finally
            {
                if (_fetch != null && !_fetch.IsDisposed) _fetch.Enabled = true;
            }
        }

        private void Submit()
        {
            _error.Text = "";
            string name = (_name.Text ?? "").Trim();
            string modelId = (_modelId.Text ?? "").Trim();

            if (name.Length == 0) { _error.Text = "显示名不能为空。"; _name.Focus(); return; }
            if (modelId.Length == 0) { _error.Text = "模型 ID 不能为空。"; _modelId.Focus(); return; }
            if (_provider.SelectedItem == null)
            {
                _error.Text = "先选一个供应商。";
                return;
            }
            if (_isNew && _registry.FindModel(name) != null)
            {
                _error.Text = "已经有同名模型了。";
                _name.Focus();
                return;
            }
            if (!_isNew && !string.Equals(name, _spec.Name, StringComparison.OrdinalIgnoreCase))
                _registry.DeleteModel(_spec.Name);

            int ctx;
            if (!int.TryParse((_ctx.Text ?? "").Trim(), out ctx) || ctx < 0) ctx = 0;

            double price;
            if (!double.TryParse((_price.Text ?? "").Trim(), out price) || price < 0) price = 0;

            _registry.UpsertModel(new ModelSpec
            {
                Name = name,
                ProviderName = _provider.SelectedItem.ToString(),
                ModelId = modelId,
                ContextWindow = ctx,
                PricePerMillionCny = price,
                Enabled = _enabled.Checked,
            });

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
