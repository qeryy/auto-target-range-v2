// 灵枢桌面版 —— 版本号（唯一来源）
//
// ## 为什么单独一个文件
//
// 版本号原来散在 **5 个地方**：
//
//   build\build.ps1          $version = '2.0.0'        ← 写进 exe 的文件版本资源
//   src\Ui\IPage.cs          AppServices.Version = "2.0.0"
//   src\Ui\Program.cs        崩溃提示里的版本
//   src\Llm\McpClient.cs     MCP 客户端信息里的 version
//   src\Tools\McpToolAdapter.cs  同上
//
// 发版时改一处、漏四处，就会出现「关于页写 2.0.0、崩溃报告写 1.9.0」这种事。
//
// `build.ps1` 里其实早就留了注释说「用 `src\Version.cs` 里的一处常量」，
// **但那个文件一直没建**，注释和现实对不上。
//
// ## 约定
//
// **只有这个文件写版本号的字面量。** 其余地方一律引用 `AppInfo.Version`；
// `build.ps1` 用正则从这个文件里读。
//
// 改版本：只改下面这一行。

namespace SpiritScout.Core
{
    /// <summary>程序的基本信息。版本号的唯一来源。</summary>
    public static class AppInfo
    {
        /// <summary>
        /// 版本号。`build.ps1` 会用正则从这一行取值写进 exe 的文件版本资源，
        /// 所以**必须保持「public const string Version = 一个字符串字面量」的形态**，
        /// 别改成拼接表达式、别加别的修饰符。
        ///
        /// 另外：这个文件里**别处不要再写** `Version = "数字.数字"` 这种片段 ——
        /// build.ps1 用的是全文正则，注释里的示例也会被匹配到（踩过：
        /// 注释里写了个示例值，结果版本号被打成了那个示例）。
        /// </summary>
        public const string Version = "2.0.0";

        /// <summary>产品名（窗口标题、关于页、MCP 客户端信息共用）。</summary>
        public const string ProductName = "灵枢 · SpiritScout";

        /// <summary>MCP 客户端标识里用的名字（协议要求 ASCII 短名）。</summary>
        public const string McpClientName = "SpiritScout-Desktop";
    }
}
