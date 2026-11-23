# 火绒误报修复说明（2026-11-22）

## 问题

火绒从 11-19 起连续 **21 次**把本项目端到端测试程序隔离为 **`TrojanSpy/MSIL.Stealer.co`**，隔离区里堆了 68 个文件共 **138 MB**（大部分是 ~2 MB 的 `e2e.exe`，每次 sha1 不同各存一份）。

## 为什么会误判（启发式特征）

报的不是文件内容，而是**整条构建链**：

```
powershell -ExecutionPolicy Bypass          ← 杀软天然警戒的脚本启动方式
  ↓ 拉起
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  ↓ 编译产物
%TEMP%\_e2e_build_xxxx\e2e.exe              ← 随机目录名 + 新建 exe
  ↓ 立刻运行
进程内再开 TcpListener 当服务器              ← 网络通信特征
```

这四条叠一起，正是「脚本调用编译器、临时目录生成 exe、立刻联网」的经典恶意构建器画像。改输出路径（从 `%TEMP%` 改到 `build\out\`）**无法根治** —— 我实测过，火绒照样报。

## 根治办法：把「编译」和「运行」拆开

| 脚本 | 职责 | 变化 |
|---|---|---|
| `build\build_once.ps1` | **只负责编译**：`csc` → `build\out\build-e2e\e2e.exe`、`build\out\build-fit\fit.exe` | 新增 |
| `build\e2e_check.ps1` | **只负责运行**：直接用 `out\build-e2e\e2e.exe` 跑断言 | 不再调 csc |
| `build\fit_check.ps1` | 同上，用 `out\build-fit\fit.exe` | 不再调 csc |

这样 check 脚本永远不再拉 `csc.exe`，「脚本→编译器→临时 exe→立刻跑」这条链就断了，火绒没有东西可以匹配。

## 白名单建议（火绒界面手动加）

在 **火绒 → 设置 → 信任区** 里加三条，范围尽量窄：

| 类型 | 内容 |
|---|---|
| 目录 | `C:\Users\admin\Desktop\全自动打靶\DesktopApp\build\` |
| 进程 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` |
| 目录 | `C:\Users\admin\Desktop\全自动打靶\` （可选，更宽） |

**不要**把 `powershell.exe`、`DSH NEXT.exe` 整个加白 —— 范围太大，等于把自家门也敞了。

## 隔离区清理（115 MB）

火绒自我保护锁死了 `C:\ProgramData\Huorong\Sysdiag\Quarantine\`（文件和 `QuarantineEx.db` 都不能从外部删），只能由火绒界面清：

```
火绒主界面 → 隔离区 → 全选 → 删除
```

删之前确认里面确实只有你自己的测试产物（本次 66 个、115 MB，全是 e2e.exe / fit.exe / 灵枢.exe）。

## 另外两个修正（顺带发现）

### 1. `AgentLoopE2E.cs` 测试目录不清理导致前三条断言假失败

`TestFirstRun` 复用上次的 `data\firstrun\` 目录，里面残留的 `models.json`/`secrets.dat` 导致 `HasModel()` 返回 `true`，让「全新安装」「只有供应商」「没填密钥」三条断言全挂。已加 `Directory.Delete(dir, true)` 先清空。

### 2. 「恢复上次页面」断言与产品行为不符

`FirstRun.DecideStartupPage` 在 11-22 刚改成**永远返回 `"home"`**（你改的产品逻辑：「默认开启时进入的界面改为任务界面」），但测试还在断言它返回 `"models"`。这是**产品改了、测试没跟上**，不是 bug。已把断言改成「传任何值都回 home」。

## 验证结果

```
build_once.ps1   → [OK] e2e.exe (1,986 KB) + fit.exe (1,968 KB)
e2e_check.ps1    → 全通过 141 项断言，退出码 0
fit_check.ps1    → 全通过 1 项断言，退出码 0
火绒新增隔离     → 0
```

## 以后源码改了怎么办

改 `tests\AgentLoopE2E.cs` 或 `tests\LayoutFit.cs` 后，先跑：

```powershell
powershell -NoProfile -File build\build_once.ps1
```

再跑对应的 check 脚本。check 脚本找不到产物时会明确提示跑哪个。

## 备份

改动前的原文件都在同目录 `.bak-20261122` 后缀：

- `e2e_check.ps1.bak-20261122`
- `QuarantineEx.db.bak-20261122`（火绒记录库，只读能看内容）

## 关于编码的坑（以后改文件注意）

这几个 `.ps1` 含中文注释，**必须存成 UTF-8 with BOM**。
之前我写成无 BOM，PowerShell 按 GBK 解码把中文弄成乱码，连带破坏 `{0:N1}` 格式化字符串，直接让脚本崩。现在三个文件都已经是 BOM。
