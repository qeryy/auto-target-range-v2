// 灵枢桌面版 —— 数据格式版本
//
// ## 为什么要版本号
//
// 现在的数据文件（config.json / sessions/*.json / models.json ...）**没有任何版本标记**。
// 这意味着：
//
//   · **新增字段** —— 兼容。反序列化时旧文件缺这个字段，得到默认值。**没问题。**
//   · **改字段名** —— 旧值静默丢失，变成默认值。**用户不知道自己的设置没了。**
//   · **改字段语义** —— 更糟。比如把 RecallLimit 从「召回几条」改成「token 预算」，
//     旧值 6 会被当成 6 token —— 行为完全不对，而且没有任何提示。
//
// 前两种情况加个版本号就能识别出来（虽然还得写迁移代码），
// 第三种至少要能**发现**问题，而不是稀里糊涂地用错。
//
// ## 设计取舍
//
// **写进文件、读出来检查，但不阻塞**：
//   · 版本比程序旧 → 静默接受（字段缺失就是默认值），记一条日志
//   · 版本比程序新 → 记日志 + 在设置页提示「数据来自更新的版本」
//
// 不弹窗。升级是常事，每次启动弹一个「版本不匹配」的框会烦死人。
// 但**必须留下痕迹**——万一真出问题，日志里能看到。
//
// ## 当前版本
//
// SchemaVersion = 1（2026-11，首次引入）。
// 之前的所有数据视作 0，读到时当作 1 处理（那些版本里没有做过破坏性改动）。

using System;

namespace SpiritScout.Core
{
    public static class DataVersion
    {
        /// <summary>当前代码期望的数据版本。</summary>
        public const int Current = 1;

        /// <summary>
        /// 检查一个数据文件的版本，把结论写进日志。
        /// </summary>
        /// <param name="what">文件用途（"config" / "session" / "models"...），只用于日志。</param>
        /// <param name="fileVersion">文件里记的版本（0 = 没有这个字段的老文件）。</param>
        /// <returns>true 表示版本正常或更旧（可以继续用）；false 表示来自更新的版本。</returns>
        public static bool Check(string what, int fileVersion)
        {
            // 老文件没有版本字段 → 读出来是 0，当作 1 处理。
            // 1 之前的那些版本里没做过破坏性改动，所以直接兼容。
            if (fileVersion <= 0) return true;

            if (fileVersion == Current) return true;

            if (fileVersion < Current)
            {
                // 比程序旧。当前没有需要迁移的（Current 还是 1），先只记日志。
                // 以后真要做迁移，这里就是插入点。
                ConfigManager.Log("数据版本：" + what + " 是 v" + fileVersion
                    + "，程序是 v" + Current + "（按兼容处理）");
                return true;
            }

            // 比程序新 —— 用户可能装了新版又降级回来。
            // 不阻止使用（结构大概率兼容），但要说一声。
            ConfigManager.Log("数据版本：" + what + " 是 v" + fileVersion
                + "，比程序（v" + Current + "）新。可能有字段读不懂。");
            return false;
        }

        /// <summary>这个版本号是不是「来自更新的程序」（用于界面提示）。</summary>
        public static bool IsFromNewer(string what, int fileVersion)
        {
            return fileVersion > Current;
        }
    }
}
