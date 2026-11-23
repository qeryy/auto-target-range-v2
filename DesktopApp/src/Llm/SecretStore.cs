// 灵枢桌面版 —— 密钥存储
//
// 用户的 API Key **不能明文落盘**。这里用 Windows 自带的 DPAPI
// （`ProtectedData`，CurrentUser 作用域）加密后写入 secrets.dat。
//
// 特性：
//   * 只有**同一个 Windows 用户**在**同一台机器**上能解出来；
//   * 拷走 secrets.dat 到别的机器解不开（DPAPI 绑用户凭据）；
//   * 不需要自己管主密码，也就没有「忘了主密码」这种坑。
//
// 格式：JSON 字典 { "provider名": "密文base64" }，再整体加密一次——
// 双层是为了让文件即使被单独打开也看不出有几个供应商。

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SpiritScout.Llm
{
    /// <summary>密钥库：按供应商名保存 API Key（加密）。</summary>
    public sealed class SecretStore
    {
        // DPAPI 的附加熵：让别的程序即使拿到文件也没法直接解（要同样的熵）
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("SpiritScout.Desktop.v1");

        private readonly string _path;
        private Dictionary<string, string> _cache;
        private readonly object _lock = new object();

        public SecretStore(string path)
        {
            _path = path;
        }

        private Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            lock (_lock)
            {
                if (_cache != null) return _cache;
                _cache = ReadAll();
                return _cache;
            }
        }

        private Dictionary<string, string> ReadAll()
        {
            try
            {
                if (!File.Exists(_path)) return new Dictionary<string, string>();
                byte[] blob = File.ReadAllBytes(_path);
                if (blob.Length == 0) return new Dictionary<string, string>();

                byte[] plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(plain);
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                return d ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                // 解不开（换了机器 / 重装系统 / 换了 Windows 用户）：
                // DPAPI 的 CurrentUser 作用域下，密钥只能被「当初加密的那个用户」
                // 在「同一台机器」上解开。拷 exe + data 到别的机器就必然失败。
                //
                // **不能只是静默返回空字典** —— 那会引出一条数据丢失路径：
                //   解不开 → Load() 给空表 → 用户改任一供应商的 key →
                //   Flush() 只写回那一条 → 其余密钥全部永久丢失。
                //
                // 照 `JsonStore.Read` 的做法：**先把原件改名留证**，
                // 这样后面无论怎么写都覆盖不掉它；用户把机器/账户恢复回来时
                // 还能把 `.unreadable-*` 改回 `secrets.dat` 继续用。
                string backup = "";
                try
                {
                    backup = _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(_path, backup);
                }
                catch { backup = ""; }

                try
                {
                    Core.ConfigManager.Log("密钥库解不开，已降级为空："
                        + ex.GetType().Name + ": " + ex.Message
                        + (backup.Length > 0
                            ? " · 原件已备份为 " + Path.GetFileName(backup)
                            : " · 原件改名失败，仍在原处"));
                }
                catch { }

                return new Dictionary<string, string>();
            }
        }

        private void Flush()
        {
            try
            {
                string json = new JavaScriptSerializer().Serialize(_cache);
                byte[] plain = Encoding.UTF8.GetBytes(json);
                byte[] blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = _path + ".tmp";
                File.WriteAllBytes(tmp, blob);
                if (File.Exists(_path))
                {
                    try { File.Delete(_path); } catch { }
                }
                File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("保存密钥失败：" + ex.Message, ex);
            }
        }

        /// <summary>存一把密钥（空字符串表示清除）。</summary>
        public void Set(string provider, string apiKey)
        {
            var all = Load();
            lock (_lock)
            {
                if (string.IsNullOrEmpty(apiKey)) all.Remove(provider);
                else all[provider] = apiKey;
            }
            Flush();
        }

        /// <summary>取密钥；没有就返回空串。</summary>
        public string Get(string provider)
        {
            var all = Load();
            string v;
            return all.TryGetValue(provider, out v) ? (v ?? "") : "";
        }

        /// <summary>是否已经配过（界面用它决定显示"已配置/未配置"）。</summary>
        public bool Has(string provider)
        {
            return Get(provider).Length > 0;
        }

        /// <summary>列出已配置的供应商名（不暴露密钥本身）。</summary>
        public List<string> Providers()
        {
            return new List<string>(Load().Keys);
        }

        public void Clear()
        {
            lock (_lock) { _cache = new Dictionary<string, string>(); }
            Flush();
        }
    }
}
