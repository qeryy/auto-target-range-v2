# 灵枢桌面版（SpiritScout Desktop）

> ## ⚠ 开发中 · 未完成
>
> 这份代码**还在迭代**，不是完工版本：界面、数据结构、检查脚本都可能变。
> 功能能跑、能过全量检查，但别当成稳定 API 依赖。
>
> 最近一次全量验证：`check_all` **11 项全过** / `verify_all` 全绿
> （`e2e` 141 断言 + 性能 6 项 + 配色 9 项 + 主题 4 项 + 会话 9 项）。

一个跑在你自己 Windows 电脑上的安全测试与 CTF 解题助手。
**单文件 exe，不需要 Python、Node、Docker 或 WebView。**

```
out\灵枢.exe        1988 KB    ← 双击就能用
```

> 这是 `SpiritScout/`（Python + FastAPI Web 版）的**独立桌面实现**，
> 纯 C# WinForms 重写，两者数据不互通、可以同时装。
> 上游 web 版见仓库根目录的 [README](../README.md)。

---

## 当前状态（258 轮，说实话）

| 维度 | 状态 |
|---|---|
| 代码 | 编译干净、44 条静态断言全绿（新增一条：11 个 Form 都要收口窗口图标与分辨率自适应）|
| **运行验证** | **✅ 2026-11-20 全量通过**：9 项结构检查 + **141 项 e2e 断言** + 排版贴合 + 6 项性能 + 9 项配色 + 静态检查全绿，`exit 0`；检查窗口全部 `--no-focus` 后台跑，不抢前台 |
| **分辨率** | **✅ 150% 缩放自适应**：字体/布局/自绘统一按 DPI 比例缩放，窗口状态按设计坐标存储；16 张截图已在 150% 下重拍 |
| **侧栏残影** | **✅ 2026-11-20 根治**：真因是 `ButtonBase` 派生（`Button`/`CheckBox`）自绘控件的 `OnPaintBackground` **根本不被调用**，双缓冲画布留着上一个同尺寸控件的像素被反复压印。铺底改到 `OnPaint` 里做（NavItem/SessionItem/FlatButton/ModeCard），实屏截图验证切页、切会话、最小化恢复后均无残影 |
| **程序图标** | **✅ 2026-11-20 修复（上一轮的结论是错的）**：`-win32icon` 只管 exe 文件图标，运行时拿不到，窗口/任务栏一直是 WinForms 的通用图标。现在构建再嵌一份 `/resource:assets\icon.ico,icon.ico`，`Theme.AppIcon()` 统一取用，`ApplyWin11` 给每个窗口设 `Form.Icon`，托盘气泡也用它。「关于 → 运行自检 → 程序图标」是这条链的检查项 |
| **开关残影** | **✅ 2026-11-20 修复**：`Switch` 继承 `CheckBox`（也是 ButtonBase 派生），和上一轮那四个控件同一个病——铺底写在不会被调用的 `OnPaintBackground` 里，通知页四个开关互相压字。铺底移进 `OnPaint`，ButtonBase 派生的五个控件现已全部收口 |
| **窗口自适应** | **✅ 2026-11-20 补齐**：`Theme.ApplyWin11` 里一次收口——DPI 缩放之后按所在屏工作区夹紧窗口（`MinimumSize` 一起夹，夹小后开 `AutoScroll` 让底部按钮还滚得到），显示后再把整窗拉回屏内。10 个对话框 + 主窗体全覆盖 |
| **按钮蓝斑** | **✅ 2026-11-20 修复**：键盘焦点圈圆角半径夹到焦点矩形半高内，消除胶囊按钮左右自交的蓝色月牙 |
| **秒出启动** | **✅ 2026-11-20 优化**：氛围背景整张位图缓存 + 卡面按实例缓存 + 首屏推迟一帧建；实测窗口出现 146ms、首屏内容就绪 308ms（原来 ~900ms 才见第一帧） |
| **全局字号** | **✅ 2026-11-20 放大两个字号**：全站字体走 `Theme.F(pt)`，一个常量 `Theme.FontBump` 调大调回；配套新增 `fit_check.ps1`（对话框/页面/气泡按控件树量纵裁·挤字·压字·出窗），页头动作条放不下自动换行 |
| **卡片黑边** | **✅ 2026-11-20 修复**：卡面投影被控件边界硬裁 + 半透明环逐次重绘再合成，贴卡边留下一条近黑带（实测 lum 5，底色 23）。改成**先铺祖先底色让卡面全不透明**、投影环数按实际留白算，边缘最深只到 lum 18，14 页截图已重拍 |
| 交付前清点 | exe 1988 KB · FileVersion 2.0.0.0 · 92 源文件 · 15 份文档 · 18 个 build 脚本 |
| web 版 | 有 **26 项未提交改动**（早期探索留下，已核查、不影响功能，[交付快照](交付快照.md) 里有还原方案）|
| 截图 | 16 张，11-19 按软面版界面**全量重拍**、DPI 修复后又在 150% 缩放下重拍（[界面清单](界面清单.md) 里逐张标注）|

**14 页 / 19 工具 / 9 agent / 6 会话标签 / 12 个窗口都实际跑过。**

复跑全量（约 3.5 分钟，会启动界面 13 次）：

```powershell
powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\verify_all.ps1')))"
```

只想确认代码没坏（8 秒，不启动程序）：

```powershell
powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\static_check.ps1')))"
```

**回归检查表** → **[待验证清单.md](待验证清单.md)**

---

## 先看哪个

| 你想做的事 | 看这份 |
|---|---|
| **动手改代码之前**（目录职责、构建交付、验证体系、UI 规范、踩过的坑）| **[项目结构与维护手册.md](项目结构与维护手册.md)** ← 维护用的总入口 |
| **第一次用**（配模型 → 建任务 → 对话）| **[使用说明.md](使用说明.md)** |
| 这版有什么、验证到什么程度 | [发布说明.md](发布说明.md) |
| 界面长什么样 | [docs/screenshots/](docs/screenshots/) 或 `发布说明.md` 的「界面」章 |
| 加一个工具 / 页面 / 智能体 | [扩展指南.md](扩展指南.md) |
| 内部实现怎么组织的 | [ARCHITECTURE.md](ARCHITECTURE.md)（604 KB，按关键词搜）|
| 验过什么、怎么验的 | [测试覆盖清单.md](测试覆盖清单.md) |
| 交付包里有什么 | [交付快照.md](交付快照.md) |
| 提交前要检查什么 | [提交前必读.md](提交前必读.md) |
| 开发流程约定 | [开发流程约定.md](开发流程约定.md) |
| 能力清单与验证状态 | [能力清单.md](能力清单.md) |
| 界面元素逐项对照 | [界面清单.md](界面清单.md) |
| 真实任务跑过的记录 | [验证记录-真实任务.md](验证记录-真实任务.md) |
| **攒了一批改动还没验** | **[待验证清单.md](待验证清单.md)** ← 按风险排序的验证步骤 |

---

## 三十秒确认它能不能跑

```powershell
cd DesktopApp
powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\build.ps1')))"      # 编译，约 1 秒
.\out\灵枢.exe                                                # 双击也行
```

第一次打开会停在「模型」页——因为还没配模型。跟着
[使用说明](使用说明.md) 第 1 步填完，就能建任务了。

---

## 规模

| 项 | 数量 |
|---|---|
| 页面 | 13 |
| 内置工具 | 19 |
| 智能体 | 9（1 个主控 + 8 个方向）|
| 源码 | 94 个文件 · 26037 行 · **零第三方依赖**（只用 .NET 4.8 自带程序集）|
| exe | 1988 KB |
| 文档 | 12 份 |

---

## 跑一遍完整检查

```powershell
# 全部（结构 + 性能 + 配色），约 3.5 分钟
powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\verify_all.ps1')))"

# 只跑结构/布局/行为前三项 + 主题，约 20 秒
powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\check_all.ps1')))"
powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\check_all.ps1'))) -Quick"   # 前 3 项，约 2 秒

# 不启动程序的静态检查，约 40 秒
powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\static_check.ps1')))"
```

**`check_all` 的 11 项**（会启动程序）：

| 检查 | 覆盖 |
|---|---|
| `web_parity` | web 版功能区块 ⊆ 桌面版页面 + 每页非空壳 |
| `tool_parity` | 工具白名单、页面图标、smoke 覆盖、模块一致性（4 条不变量）|
| `doc_check` | **14 份**文档的数字与代码一致 + 图片/链接可达 + 对话框字段齐全 |
| `e2e_check` | 起假 LLM 服务跑完整 AgentLoop（**141 项断言**，11-19 实测全通过）|
| `smoke` | 14 页逐页打开、关键元素在位 |
| `layout_check` | 控件不越界、无零尺寸 |
| `clip_check` | 文字不被裁 |
| `overflow_check` | 内容不超出窗口 |

另外三个（`verify_all` 会一起跑）：

| 脚本 | 内容 | 耗时 |
|---|---|---|
| `perf_check` | 渲染性能 6 项（打开大会话、重绘、滚动）| ~40 秒 |
| `color_check` | 配色 9 项（实际像素 vs 理论混色值）| ~2 秒 |
| `theme_check` | 深浅主题 4 项（启动 + 运行时切换，看侧栏主色占比）| ~11 秒 |
| `session_ui_check` | 会话端到端 9 项（真机发一条，只判结果）| ~15 秒 |
| `static_check` | **不启动程序**：编译零警告、async void、GDI、圆角、UI 一致性、死代码，共 10 节 | ~40 秒 |

报告写到 `out\reports\`，`out\` 根目录只留 exe。

---

## 设计取向

**零第三方依赖**——只用 .NET Framework 4.8 自带的东西（`System.Web.Extensions`
做 JSON、`System.Drawing` 做绘图、`DwmSetWindowAttribute` 做 Win11 圆角）。
代价是有些东西要自己实现（ZipCrypto 解密、magic number 识别、熵计算），
好处是**一个 1988 KB 的 exe 拷到任何装了 .NET 4.8 的 Windows 上就能跑**。

**配色与圆角参照 [SaltUI](https://github.com/Moriafly/SaltUI) 的设计 token**
（它是 Kotlin/Compose 的库，C# 里引用不了，所以是逐值对齐而不是引入依赖）。
窗口「chrome」也一并对齐：`DwmSetWindowAttribute` 的 `DWMWA_USE_IMMERSIVE_DARK_MODE`
让标题栏/边框变暗（不再是浅色标题栏压深色界面），侧栏品牌区置顶、导航选中态用 web 同款
`accent-soft` 软高亮 + 亮蓝字，输入框换深色描边并带占位提示。

**首页对齐 web 版 home（2026-11-19 重做）**：不再是会话表格，而是「下达任务」入口——
居中内容栏浮在氛围光背景上（蓝 / 紫 / 琥珀三团缓光 + 细噪点，`AmbientPanel`），
eyebrow「新建任务」+ 渐变大标题「下达任务，剩下的交给 *灵枢*」（楷体 em 用
`GraphicsPath` + `LinearGradientBrush` 画蓝→金渐变）+ 三张按模式着色的模式卡
（SRC 蓝 / CTF 绿 / 竞赛紫，选中同色描边 + 实心单选点）+ 无缝任务描述 composer
（单卡片内分隔线 + 附件 / 项目下拉 / + 项目 / 开始执行，聚焦时描边泛蓝）+
示例 chips + 授权提示。历史会话移到侧栏「会话」区：双行项（目标 + 模式色点），
hover 出 × 可删；侧栏整体比内容区深一档（`Theme.Sidebar`）拉开层次。
首屏落在首页而非强制模型页。

**会话页**：新会话空态给一句下一步指引（不再一片黑）；气泡按 Markdown 分块渲染、
宽度随窗宽重测（构造时 docking 未布局量出的窄宽度会在首次布局后重建）；
底部输入区是单张无缝卡片 + 蓝色发送钮。

**深色自绘控件补齐**（系统原生控件在深色主题是白斑/白边，逐个换掉）：
`DarkTextBox`（深色描边 + 多行占位自绘）、`DarkSelect`（首页/会话页的自绘下拉）、
`DarkCombo`（工厂下拉接管 WM_PAINT/WM_PRINT，连箭头按钮一起自绘——
DropDownList 的显示区和按钮归系统主题画，BackColor 和 OwnerDraw 都管不到按钮）、
`DarkTabs`（会话页标签条 + 页框整条重铺，选中页亮蓝 underline）、`ModeCard` /
`SessionItem` / `HeroTitle`。原生滚动条用 `SetWindowTheme(DarkMode_Explorer)`
整棵控件树压暗（`Theme.DarkScrollTree`，对话框经 `ApplyWin11` 自动挂）。
旧的「新建任务」对话框已被首页 composer 取代，`NewTaskDialog` 死代码删除。

**全页软面化（2026-11-19 第二轮，用户要求「不要全是方框、要柔顺、板块分类」）**：
共享原语换成无边框软卡语言——`Card` 默认 0 描边 + 16 圆角 + 柔投影 + 顶部
分类色晕渐隐（`Tint`），色晕里放 `HueTile` 首字/符号头像块；`SectionCard`
把「标题 + 副标题 + 内容」收成一个分区块（12 页全部改用），`MetricTile`
是指标软块，`SidePanel` 给侧栏铺渐变底。表格去外框去网格线改斑马纹 +
软悬停；卡片不做 hover 抬升（父容器 MouseLeave 被子控件打断会闪），
交互反馈放在行/按钮/导航/选中描边上。曾评估 WebView2 嵌入 web 版——
那要带 Edge 运行时，违反「单 exe 零依赖」，否决；首页已证明纯 GDI+
自绘能做到同等观感。`docs/screenshots/` 13 张页面图由 `build/shots.ps1` 复跑重拍
（第九轮字号 +2 后已全量重拍一次）。

**`SpiritScout/` 一行没动**——那个是参照物，不是可改的目标。

**侧栏残影修复（2026-11-20 第七轮）**：用户报「渲染卡很久 + 侧边栏文字残影」。
逐项实测：进程内部启动 ~130ms、页面构建 8–90ms、150 条消息的会话列表
滚轮每格处理 0–1ms——**卡顿不在代码路径里**，真因是侧栏会话列表用了
`Transparent + AutoScroll` 面板：滚动是 ScrollWindowEx 平移旧像素，新暴露
条带走「父窗模拟透明」补绘，补出来的渐变和旧像素里烘焙的渐变错位，
文字就拖影。改为实底 `Theme.Sidebar`（该区域离顶部光晕远，观感无差别）+
每次滚动后整表重绘（`SessListPanel`）。排查用的全部临时插桩已移除。

**秒出启动（2026-11-20 第八轮）**：用户要求「基本秒出而不是加载好几秒」。
首帧大头是软件渲染的装饰层：三团 PathGradient 大光晕 + 整屏噪点每帧 ~100ms，
每张卡的多层投影每帧 ~8ms。改成绘制缓存——氛围层内容只由窗口尺寸决定，
全窗口共用一张 `Format32bppPArgb` 位图；卡面由（尺寸+外观参数）决定，
60000px 以上的卡按实例缓存、每帧只剩一次 blit。另外首屏页面从 Load 里
推迟一帧再建：窗口先出壳（导航/侧栏可见），内容随即填充。实测
Main→窗口显示 146ms、首屏内容就绪 308ms；截图模式保持同步等待不受影响。
测量插桩用完即删，verify_all 全绿。

**全局字号 +2 与排版适配（2026-11-20 第九轮）**：用户要求「文本内容都放大两个
字号，并且进行页面适配」。做法是**一个旋钮**而不是 92 个文件：全站字体都从
`Theme.F(pt)` 出去，那里加常量 `Theme.FontBump = 2F`（`Theme.cs`），
`Fpx()` 统一 `(pt + FontBump) * 96/72` 再乘 `UiScale`。实测本机 150% 屏正文 24px。
字号变大后坏的地方全在**自绘几何和对话框**（UIA 那几项检查看不见），所以补了
`build/fit_check.ps1` + `build/tests/LayoutFit.cs`：离屏构造 10 个对话框、14 个页面、
9 种会话气泡，按控件树自己量四类问题——纵裁（文字高度 > 盒子）、挤字（单行控件
放不下）、压字（同层两个控件矩形互盖，按文字实际墨迹算）、出窗（掉到内容区外，
AutoScroll 容器里能滚到的不算）。它抓到并修掉三处：`SkillDialog` 标签列宽顶进输入框、
`ModelDialog` 说明文字压在「从端点拉取」按钮上、`Theme.PageHeader` 的动作条在窄窗口
一行放不下时把最左的按钮顶到标题上（现在排到标题右边放不下就自动换行，说明行
跟着往下走，页头高度按实测行高算）。fit_check 已并入 `check_all.ps1` 第 9 项。
页面像素重拍改用 `build/shots.ps1`（走 exe 的 `--screenshot`，真窗口渲染）。

---

## 数据在哪

```
%APPDATA%\SpiritScout\
├── config.json      偏好
├── models.json      供应商与模型（不含密钥）
├── secrets.dat      密钥（DPAPI 加密，拷到别的机器解不开）
├── sessions\        会话
├── logs\            日志（按天分，留最近 5 份、每份 ≤ 8MB）
└── experience.json  积累的经验
```

AppData 写不进去时自动退到 **exe 同级的 `data\`**。实际位置在「设置」页第一行。
