# AGENTS.md

## Windows 终端规范

- 所有 Windows 本地命令必须使用 PowerShell 语法，并将命令工具的 `shell` 参数设为
  `powershell`。
- 禁止在 Windows 本地命令中选择 Bash。
- 只有命令明确针对 WSL 或 Linux，且用户明确要求该环境时，才允许使用 Bash。
- 如果 PowerShell 命令失败且输出显示 `/bin/bash`、`bash: ... not found` 或类似的
  shell 不匹配错误，必须将本次调用认定为代理的 shell 选择错误，并立即改用
  PowerShell 重跑。
- Bash 误调用产生的结果不得计作项目验证、测试或排障证据。

产品工程约束见 [docs/AGENTS-upstream.md](docs/AGENTS-upstream.md)。
