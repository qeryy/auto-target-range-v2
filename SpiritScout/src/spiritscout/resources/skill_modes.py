"""技能按任务模式分类：SRC 渗透 / CTF 解题 / 智能体竞赛。

技能库里存的是**技术领域**（domain: web/reverse/crypto…），而任务界面选的是
**工作模式**。两者不是一对一——web 类技能在 SRC 和 CTF 都用得上，逆向类只在
CTF 用。这层映射放在运行时算，不去改 1 万多行的 skills.yaml（那会把注释冲掉）。

新增 domain 时只要往 ``_MODES`` 里加一行；没登记的落到 ``_DEFAULT_MODES``，
宁可多给也不让 Agent 手里空着。
"""

from __future__ import annotations

# 任务模式（与首页三张卡一一对应）
MODE_SRC = "src"
MODE_CTF = "ctf"
MODE_COMPETITION = "competition"

ALL_MODES = (MODE_SRC, MODE_CTF, MODE_COMPETITION)

#: 三张卡在界面上的叫法
MODE_LABELS = {
    MODE_SRC: "SRC 渗透测试",
    MODE_CTF: "CTF 解题",
    MODE_COMPETITION: "智能体竞赛",
}

#: domain → 适用的模式。交叉的（web/api/mobile/cloud）两边都给。
_MODES: dict[str, tuple[str, ...]] = {
    # —— 渗透与 CTF 都用 ——
    "web": (MODE_SRC, MODE_CTF),
    "api": (MODE_SRC, MODE_CTF),
    "mobile": (MODE_SRC, MODE_CTF),
    "cloud": (MODE_SRC, MODE_CTF),
    "network": (MODE_SRC, MODE_CTF),
    "payloads": (MODE_SRC, MODE_CTF),
    "tools": (MODE_SRC, MODE_CTF),
    "hardware": (MODE_SRC, MODE_CTF),
    "supply-chain": (MODE_SRC, MODE_CTF),
    "iot": (MODE_SRC, MODE_CTF),
    "automation": (MODE_SRC, MODE_CTF),
    "utility": (MODE_SRC, MODE_CTF),
    "malware": (MODE_SRC, MODE_CTF),
    # —— 只在渗透 ——
    "recon": (MODE_SRC,),
    "privesc": (MODE_SRC,),
    "social-eng": (MODE_SRC,),
    "tradecraft": (MODE_SRC,),
    "wireless": (MODE_SRC,),
    "reporting": (MODE_SRC,),
    "defense": (MODE_SRC,),
    "pentest": (MODE_SRC,),
    "src": (MODE_SRC,),
    "evasion": (MODE_SRC,),
    "code-review": (MODE_SRC,),
    # ad 在企业内网和竞赛里都有
    "ad": (MODE_SRC, MODE_COMPETITION),
    # —— 只在 CTF ——
    "ctf": (MODE_CTF,),
    "cryptography": (MODE_CTF,),
    "crypto": (MODE_CTF,),
    "reverse-engineering": (MODE_CTF,),
    "reverse": (MODE_CTF,),
    "exploit-dev": (MODE_CTF,),
    "misc": (MODE_CTF,),
    "ai-ml": (MODE_CTF,),
    "ai": (MODE_CTF,),
    "forensics": (MODE_CTF,),
    "pwn": (MODE_CTF,),
    "game": (MODE_CTF,),
    "custom": (MODE_CTF,),
    # —— 竞赛：赛方规则连续执行，题目本身仍是 CTF 方向 ——
    "competition": (MODE_COMPETITION, MODE_CTF),
}

#: 没登记的 domain：三类都给（宁可多给，不让 Agent 手里空着）
_DEFAULT_MODES = ALL_MODES

#: skill_id 前缀 → domain 的兜底（有的技能只带 id 不带 domain）
_ID_PREFIX_DOMAIN = (
    ("competition-", "competition"),
    ("ctf-", "ctf"),
    ("src-", "src"),
    ("ad-", "ad"),
    ("api-", "api"),
    ("ai-", "ai-ml"),
    ("mobile-", "mobile"),
    ("cloud-", "cloud"),
    ("net-", "network"),
    ("recon-", "recon"),
    ("rev-", "reverse"),
    ("pwn-", "pwn"),
    ("crypto-", "crypto"),
)


def domain_of(skill: dict) -> str:
    """取技能的技术领域：优先 domain 字段，其次按 id 前缀猜。"""
    domain = str(skill.get("domain") or "").strip().lower()
    if domain:
        return domain
    sid = str(skill.get("skill_id") or skill.get("id") or "").strip().lower()
    for prefix, dom in _ID_PREFIX_DOMAIN:
        if sid.startswith(prefix):
            return dom
    return ""


def modes_for(skill: dict) -> list[str]:
    """这条技能适用于哪些任务模式。"""
    return list(_MODES.get(domain_of(skill), _DEFAULT_MODES))


def filter_by_mode(skills: list[dict], mode: str) -> list[dict]:
    """只留当前任务模式用得上的技能；mode 为空或未知时原样返回。

    未知 mode 不筛，是因为「筛错」比「多给」贵：Agent 手里少一条技能可能整题
    做不出来，多给一条只是多占几百 token。
    """
    key = str(mode or "").strip().lower()
    if key not in ALL_MODES:
        return list(skills)
    if key == MODE_COMPETITION:
        # 竞赛的题目方向仍然是 CTF（web/crypto/reverse/pwn 都有），只是多了一层
        # 「按赛方规则连续执行」。所以竞赛模式 = CTF 池 + competition 专属。
        return [s for s in skills
                if MODE_COMPETITION in modes_for(s) or MODE_CTF in modes_for(s)]
    return [s for s in skills if key in modes_for(s)]


def summary(skills: list[dict]) -> dict[str, int]:
    """按模式统计条数，给控制台显示用。"""
    out = {m: 0 for m in ALL_MODES}
    for skill in skills:
        for m in modes_for(skill):
            out[m] += 1
    return out
