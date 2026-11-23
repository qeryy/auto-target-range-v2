"""把技能库转成桌面版可直接读的 JSON。

## 为什么需要这一步

web 端的技能库是 `configs/skills.yaml`（772 KB），.NET Framework 4.8 没有
YAML 解析器，硬引一个第三方库就违背「单 exe、零依赖」。

做法：构建前用这个脚本把 YAML 转成 JSON，打进 exe 的资源里。
转换是**单向且可重复**的，技能库改了重跑一次即可。

## 输出

  DesktopApp/src/Resources/skills.json

结构：
  {"skills": [{"skill_id":..., "domain":..., "description":..., "body":...,
               "severity":..., "cwe":..., "enabled":...}]}
"""

from __future__ import annotations

import json
import pathlib
import sys

# 本脚本在 DesktopApp/build/ 下：
#   __file__            → DesktopApp/build/export_skills.py
#   parent              → DesktopApp/build
#   parent.parent       → DesktopApp
#   parent.parent.parent → 仓库根（含 SpiritScout 与 DesktopApp）
DESKTOP = pathlib.Path(__file__).resolve().parent.parent
ROOT = DESKTOP.parent
REPO = ROOT / "SpiritScout"

SRC_YAML = REPO / "configs" / "skills.yaml"
BUILTIN = REPO / "src" / "spiritscout" / "knowledge" / "data" / "security_skills.json"
OUT = DESKTOP / "src" / "Resources" / "skills.json"

FIELDS = ("skill_id", "domain", "description", "body", "severity", "cwe",
          "tools", "source", "path", "enabled")


def load_yaml_skills(path: pathlib.Path) -> list[dict]:
    """只解析这个文件用到的 YAML 子集（顶层 skills: 列表 + 标量/多行字符串）。"""
    if not path.exists():
        print(f"  跳过（不存在）：{path}")
        return []

    try:
        import yaml  # type: ignore
    except ImportError:
        print("  需要 pyyaml：pip install pyyaml")
        return []

    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        return []
    items = data.get("skills") or []
    out = []
    for it in items:
        if not isinstance(it, dict):
            continue
        row = {k: it.get(k) for k in FIELDS if k in it}
        # tools 可能是 list，统一成逗号串
        if isinstance(row.get("tools"), list):
            row["tools"] = ", ".join(str(x) for x in row["tools"])
        row.setdefault("enabled", True)
        row.setdefault("source", "custom")
        out.append(row)
    return out


def load_builtin(path: pathlib.Path) -> list[dict]:
    if not path.exists():
        print(f"  跳过（不存在）：{path}")
        return []
    data = json.loads(path.read_text(encoding="utf-8"))
    items = data if isinstance(data, list) else data.get("skills") or []
    out = []
    for it in items:
        if not isinstance(it, dict):
            continue
        row = {k: it.get(k) for k in FIELDS if k in it}
        if isinstance(row.get("tools"), list):
            row["tools"] = ", ".join(str(x) for x in row["tools"])
        row.setdefault("enabled", True)
        row.setdefault("source", "builtin")
        out.append(row)
    return out


def main() -> int:
    custom = load_yaml_skills(SRC_YAML)
    builtin = load_builtin(BUILTIN)
    print(f"  自定义技能：{len(custom)} 条")
    print(f"  内置技能  ：{len(builtin)} 条")

    # 按 skill_id 去重（自定义优先，可覆盖内置同名）
    merged: dict[str, dict] = {}
    for row in builtin:
        sid = str(row.get("skill_id") or "").strip()
        if sid:
            merged[sid] = row
    for row in custom:
        sid = str(row.get("skill_id") or "").strip()
        if sid:
            merged[sid] = row

    skills = sorted(merged.values(), key=lambda r: (str(r.get("domain") or ""), str(r.get("skill_id") or "")))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({"skills": skills}, ensure_ascii=False, indent=1), encoding="utf-8")

    size = OUT.stat().st_size
    print(f"  写出：{OUT.relative_to(REPO.parent)}  {size / 1024:.0f} KB · {len(skills)} 条")

    domains: dict[str, int] = {}
    for s in skills:
        d = str(s.get("domain") or "未分类")
        domains[d] = domains.get(d, 0) + 1
    print("  领域分布：")
    for d, n in sorted(domains.items(), key=lambda x: -x[1]):
        print(f"    {d:14s} {n}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
