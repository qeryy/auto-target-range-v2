// 灵枢桌面版 —— 模型注册表
//
// 两段结构（与 web 版一致）：
//   providers = 供应商（一个端点 + 一把密钥）
//   models    = 该供应商下可用的模型
//
// 存储：models.json 里 {"providers":[...], "models":[...]}；
// 密钥不进这个文件，单独由 SecretStore 加密保存。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Llm
{
    /// <summary>一个可用模型。</summary>
    public sealed class ModelSpec
    {
        public string Name { get; set; }          // 显示名 / 唯一键
        public string ProviderName { get; set; }  // 指向 ProviderSpec.Name
        public string ModelId { get; set; }       // 供应商侧的模型 id
        public int ContextWindow { get; set; }
        public bool Enabled { get; set; }

        /// <summary>
        /// 参考单价：每百万 token 多少钱（人民币）。
        ///
        /// 为什么要用户自己填：各家定价差得远，而且经常调；写死一堆官方价格只会
        /// 越来越不准。填 0 表示不估算——仪表盘和用量页就不显示成本列。
        ///
        /// 只用总 token 乘这一个数，不区分输入/输出——大多数端点输入输出差价
        /// 在一倍以内，估个量级够用；要精确到分就得记录 usage 的明细，
        /// 那要改 AgentLoop 的记账逻辑，收益不值。
        /// </summary>
        public double PricePerMillionCny { get; set; }

        public ModelSpec()
        {
            Name = "";
            ProviderName = "";
            ModelId = "";
            ContextWindow = 32768;
            Enabled = true;
        }
    }

    /// <summary>models.json 的结构。</summary>
    public sealed class ModelFile
    {
        public List<ProviderSpec> Providers { get; set; }
        public List<ModelSpec> Models { get; set; }

        public ModelFile()
        {
            Providers = new List<ProviderSpec>();
            Models = new List<ModelSpec>();
        }
    }

    /// <summary>注册表：增删改查 + 调用。</summary>
    public sealed class ModelRegistry
    {
        private readonly string _path;
        private readonly SecretStore _secrets;
        private readonly ChatClient _client;
        private readonly object _lock = new object();
        private ModelFile _cache;

        public ModelRegistry(string modelsPath, SecretStore secrets)
        {
            _path = modelsPath;
            _secrets = secrets;
            _client = new ChatClient();
        }

        private ModelFile Load()
        {
            if (_cache != null) return _cache;
            lock (_lock)
            {
                if (_cache != null) return _cache;
                _cache = Core.JsonStore.Read<ModelFile>(_path, null) ?? new ModelFile();
                if (_cache.Providers == null) _cache.Providers = new List<ProviderSpec>();
                if (_cache.Models == null) _cache.Models = new List<ModelSpec>();
                return _cache;
            }
        }

        private void Flush()
        {
            Core.JsonStore.Write(_path, _cache);
        }

        // ---------- 查询 ----------

        public List<ProviderSpec> Providers()
        {
            return new List<ProviderSpec>(Load().Providers);
        }

        public List<ModelSpec> Models()
        {
            return new List<ModelSpec>(Load().Models);
        }

        public ModelSpec FindModel(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Load().Models.FirstOrDefault(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public ProviderSpec FindProvider(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Load().Providers.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public bool HasKey(string providerName)
        {
            return _secrets.Has(providerName);
        }

        // ---------- 修改 ----------

        public void UpsertProvider(ProviderSpec spec)
        {
            var f = Load();
            lock (_lock)
            {
                int i = f.Providers.FindIndex(
                    p => string.Equals(p.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) f.Providers[i] = spec;
                else f.Providers.Add(spec);
            }
            Flush();
        }

        public void DeleteProvider(string name)
        {
            var f = Load();
            lock (_lock)
            {
                f.Providers.RemoveAll(
                    p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                // 关联模型一并删掉，避免出现指向不存在供应商的孤儿模型
                f.Models.RemoveAll(
                    m => string.Equals(m.ProviderName, name, StringComparison.OrdinalIgnoreCase));
            }
            _secrets.Set(name, null);
            Flush();
        }

        public void UpsertModel(ModelSpec spec)
        {
            var f = Load();
            lock (_lock)
            {
                int i = f.Models.FindIndex(
                    m => string.Equals(m.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) f.Models[i] = spec;
                else f.Models.Add(spec);
            }
            Flush();
        }

        /// <summary>
        /// 调整模型在列表里的位置。`delta` = -1 上移 / +1 下移。
        /// 已经在两端就返回 false（什么都不做）。
        ///
        /// **列表顺序是有意义的**：`DefaultModelName()` 取第一个 `Enabled` 的模型，
        /// 「编队」页里没指派模型的 agent 就用它。所以这个顺序决定「默认用哪个模型」。
        ///
        /// web 版是靠拖拽做的（`保存顺序`）；桌面版不做拖拽 ——
        /// `DataGridView` 上实现拖拽排序要处理 `AllowDrop`、`DragEnter/DragOver/DragDrop`、
        /// 拖动时的插入指示线、以及和选中态的冲突，收益不值那个复杂度。
        /// **两个按钮达到同样目的。**
        /// </summary>
        public bool MoveModel(string name, int delta)
        {
            if (string.IsNullOrEmpty(name) || delta == 0) return false;
            var f = Load();
            bool moved = false;
            lock (_lock)
            {
                int i = f.Models.FindIndex(
                    m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                int j = i + delta;
                if (i >= 0 && j >= 0 && j < f.Models.Count)
                {
                    var tmp = f.Models[i];
                    f.Models[i] = f.Models[j];
                    f.Models[j] = tmp;
                    moved = true;
                }
            }
            if (moved) Flush();
            return moved;
        }

        public void DeleteModel(string name)
        {
            var f = Load();
            lock (_lock)
            {
                f.Models.RemoveAll(
                    m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            Flush();
        }

        public void SetKey(string providerName, string apiKey)
        {
            _secrets.Set(providerName, apiKey);
        }

        // ---------- 调用 ----------

        /// <summary>用指定模型发一次对话。</summary>
        /// <summary>
        /// 从供应商端点拉取可用模型列表（GET {baseUrl}/models）。
        ///
        /// 为什么需要这个：本地推理（LM Studio / Ollama / vLLM）的模型名是文件名
        /// 或用户自己起的名字（`qwen3.5-9b`、`path/to/model.gguf`），猜不出来。
        /// 云端也常有一堆带日期后缀的名字。让用户去后台抄名字不现实，直接问端点。
        ///
        /// 返回的列表为空、或端点不支持 /models，都当成"拿不到"——
        /// 用户仍然可以手填。
        /// </summary>
        public async Task<List<string>> FetchModelsAsync(string providerName,
            CancellationToken ct = default(CancellationToken))
        {
            var names = new List<string>();
            var provider = FindProvider(providerName);
            if (provider == null) return names;

            try
            {
                string key = _secrets.Get(provider.Name);
                var client = _client ?? new ChatClient();
                var raw = await client.GetRawAsync(provider, key,
                    "/models", ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw)) return names;

                var root = Core.Json.AsDict(Core.Json.Parse(raw));
                var data = Core.Json.Array(root, "data") ?? Core.Json.Array(root, "models");
                if (data == null) return names;

                foreach (var item in data)
                {
                    var d = Core.Json.AsDict(item);
                    if (d == null) continue;
                    // OpenAI 风格用 id，Ollama 用 name
                    string id = Core.Json.Str(d, "id");
                    if (id.Length == 0) id = Core.Json.Str(d, "name");
                    if (id.Length > 0 && !names.Contains(id)) names.Add(id);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("拉取模型列表失败(" + providerName + "): " + ex.Message);
            }
            return names;
        }

        public async Task<ChatReply> ChatAsync(string modelName, ChatRequest req,
            CancellationToken ct = default(CancellationToken))
        {
            var model = FindModel(modelName);
            if (model == null)
            {
                return new ChatReply { Ok = false, Error = "没有找到模型「" + modelName + "」，先去「模型」页添加。" };
            }
            var provider = FindProvider(model.ProviderName);
            if (provider == null)
            {
                return new ChatReply { Ok = false, Error = "模型「" + modelName + "」指向的供应商「"
                    + model.ProviderName + "」不存在。" };
            }
            req.Model = model.ModelId;
            string key = _secrets.Get(provider.Name);
            return await _client.CompleteAsync(provider, key, req, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 连通性测试：发一句最短的话，看端点是否真的能用。
        /// 返回 (是否成功, 说明文字)。
        /// </summary>
        public async Task<KeyValuePair<bool, string>> TestAsync(string modelName)
        {
            var req = new ChatRequest
            {
                Messages = new List<ChatMessage>
                {
                    new ChatMessage("user", "回复两个字：可用"),
                },
                Temperature = 0,
                MaxTokens = 16,
            };
            var reply = await ChatAsync(modelName, req).ConfigureAwait(false);
            if (reply.Ok)
            {
                string text = (reply.Text ?? "").Trim();
                if (text.Length > 40) text = text.Substring(0, 40) + "…";
                return new KeyValuePair<bool, string>(true,
                    "连通正常（" + reply.TotalTokens + " tokens）：" + text);
            }
            return new KeyValuePair<bool, string>(false, reply.Error);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
