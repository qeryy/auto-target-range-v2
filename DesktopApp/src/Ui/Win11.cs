// 灵枢桌面版 —— Windows 11 原生外观
//
// ## 为什么单独一个文件
//
// SaltUI（Moriafly/SaltUI，Compose Multiplatform）里有 `GlassEffect` 和
// `SaltShapes.dialogCorner = 20.dp`。那是 Kotlin 库，C# 里引用不了，
// 但 **Windows 11 本身就能做这两件事**——用系统 API：
//
//   · `DWMWA_WINDOW_CORNER_PREFERENCE`（33）→ 圆角窗口
//   · `DWMWA_SYSTEMBACKDROP_TYPE`（38）    → Mica / Acrylic 背景
//
// 这比自己画圆角窗口靠谱得多：系统会给窗口加阴影、处理 DPI、跟随
// 系统主题的圆角半径，而且不必自绘非客户区。
//
// ## 兼容性
//
// 两个 API 都只在 Windows 11（build 22000+）上有效。
// 在 Win10/7 上调用会返回非 0 的 HRESULT —— **不抛异常**，直接忽略即可，
// 窗口保持系统默认外观。所以调用方不用判断版本，直接调。
//
// 实测环境：Windows 11 专业版 build 26200。

using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SpiritScout.Ui
{
    /// <summary>Windows 11 的窗口圆角与背景材质。失败时静默降级。</summary>
    public static class Win11
    {
        // dwmapi.dll 的 DwmSetWindowAttribute
        // 返回 HRESULT：0 = 成功，非 0 = 不支持（Win10 及更早）
        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        // 圆角偏好（Windows 11 build 22000+）
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DEFAULT = 0;     // 系统默认
        private const int DWMWCP_DONOTROUND = 1;  // 直角
        private const int DWMWCP_ROUND = 2;       // 圆角（小）
        private const int DWMWCP_ROUNDSMALL = 3;  // 圆角（更小）

        // 背景材质（Windows 11 build 22621+）
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_AUTO = 0;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_MAINWINDOW = 2;      // Mica
        private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
        private const int DWMSBT_TABBEDWINDOW = 4;    // Mica Alt

        // 深色标题栏（Win10 1809+ 用 19，Win10 2004+/Win11 用 20）。
        // 整个应用是深色主题，但系统标题栏默认跟随「系统浅色」，
        // 于是一条亮白的标题栏压在深色界面上，非常割裂。打开沉浸深色
        // 让标题栏/边框变成近黑，与 Theme.Bg 融为一体。
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // ===== 版本检测 =====
        //
        // **不能用 Environment.OSVersion**：程序没在 manifest 里声明 supportedOS 时，
        // GetVersionEx 会走兼容模式，返回 6.2.9200（Windows 8）。实测环境是
        // Windows 11 build 26200，但 Environment.OSVersion.Version.Build 报 9200 ——
        // 于是「Win11 特性是否可用」的判断永远为 false（155 轮踩到）。
        //
        // RtlGetVersion 走 ntdll，不受兼容模式影响，拿到的才是真实版本。
        [StructLayout(LayoutKind.Sequential)]
        private struct RTL_OSVERSIONINFOW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW lpVersionInformation);

        private static int _realBuild = -1;

        /// <summary>系统真实 build 号（不受 manifest 兼容模式影响）。</summary>
        public static int RealBuild
        {
            get
            {
                if (_realBuild >= 0) return _realBuild;
                try
                {
                    var vi = new RTL_OSVERSIONINFOW();
                    vi.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOW));
                    vi.szCSDVersion = "";
                    int hr = RtlGetVersion(ref vi);
                    _realBuild = hr == 0 ? (int)vi.dwBuildNumber : 0;
                }
                catch { _realBuild = 0; }
                return _realBuild;
            }
        }

        /// <summary>窗口圆角是否可用（Win11 build 22000+）。</summary>
        public static bool SupportsRounding { get { return RealBuild >= 22000; } }

        /// <summary>Mica 背景是否可用（Win11 build 22621+）。</summary>
        public static bool SupportsMica { get { return RealBuild >= 22621; } }

        /// <summary>
        /// 给窗口加系统圆角 + 深色标题栏。
        /// **这是对话框外观最省事的改法**：不用自绘非客户区、不用手动画阴影。
        /// 深色标题栏一并在这里打开，保证主窗口与所有对话框的「chrome」
        /// 颜色统一（都是深色），不会出现个别窗口亮标题栏。
        /// </summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            SetAttr(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
            DarkTitleBar(hwnd);
        }

        /// <summary>
        /// 沉浸式深色标题栏。先试新属性（20），失败再试旧属性（19），
        /// 两个都不支持（Win10 1809 以前）就保持系统默认——静默降级。
        /// </summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int hr = SetAttrRaw(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            if (hr != 0) SetAttrRaw(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, 1);
        }

        /// <summary>直角（某些场合需要，比如要和自绘背景严丝合缝时）。</summary>
        public static void SquareCorners(IntPtr hwnd)
        {
            SetAttr(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND);
        }

        /// <summary>
        /// Mica 背景（桌面壁纸透过窗口模糊显示）。
        ///
        /// **注意：只有窗口背景是透明/半透明时才看得见。** 我们的页面全是
        /// 不透明 Panel，所以主窗口用 Mica 会被完全遮住 —— 这是正常的，
        /// 不是失效。要在设置页做开关时需要配合把 Bg 调成带 alpha 的颜色。
        /// </summary>
        public static void Mica(IntPtr hwnd)
        {
            SetAttr(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW);
        }

        /// <summary>Acrylic（比 Mica 更「磨砂」，透明感更强）。</summary>
        public static void Acrylic(IntPtr hwnd)
        {
            SetAttr(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW);
        }

        /// <summary>关掉背景材质（回到纯色）。</summary>
        public static void NoBackdrop(IntPtr hwnd)
        {
            SetAttr(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE);
        }

        /// <summary>
        /// 一次配好：圆角 + （可选）Mica。
        /// 失败静默 —— 老系统上窗口就是普通直角窗，不影响使用。
        /// </summary>
        public static void Apply(IntPtr hwnd, bool mica)
        {
            if (hwnd == IntPtr.Zero) return;
            RoundCorners(hwnd);
            if (mica) Mica(hwnd);
        }

        private static void SetAttr(IntPtr hwnd, int attr, int value)
        {
            SetAttrRaw(hwnd, attr, value);
        }

        private static int SetAttrRaw(IntPtr hwnd, int attr, int value)
        {
            if (hwnd == IntPtr.Zero) return unchecked((int)0x80070006);
            try
            {
                int v = value;
                // 返回值是 HRESULT，非 0 表示这台系统不支持 —— 不用管
                return DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int));
            }
            catch
            {
                // 极端情况下（dwmapi 缺失）也不该影响窗口显示
                return unchecked((int)0x80004005);
            }
        }
    }
}
