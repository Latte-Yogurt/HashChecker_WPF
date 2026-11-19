#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""资源键完整性 + 语言纯度检查。

与 localization_check.py 的分工
-------------------------------
`localization_check.py` 查的是**结构**：三语键集合是否一致、`{n}` 占位符数量是否匹配。
它有一个盲区：**三份词典同时写错同一个键时，它会报「一致」**。

本工具补三个结构检查看不到的：

  LK1  代码取用的键必须存在于词典 —— 三语都缺同一个键时，
       界面会静默回落到硬编码文案或裸键名，而结构检查照样报通过。
  LK2  **语言纯度**：某一语言的文本里混入了别种语言的文字。
       本项目真实存在这个问题：en-US 段有 10 条值里嵌着中文「摘要」，
       于是英文界面上会出现 `Every file in the list already has a 摘要 value.`
       这种半中半英的句子。结构检查完全发现不了。
  LK3  同一键在不同语言里的「形状」差异过大（例如一个是空串、另一个是长句），
       往往意味着某一份漏翻了。

识别方式
--------
按 Unicode 区块判断文字种类，而不是按字符白名单：
  * 汉字（CJK 统一表意文字 + 扩展 A + 兼容区）
  * 假名（平假名 + 片假名）
  * 谚文（EUC-KR 那类）
  * 拉丁字母
一个语言段里出现了它本不该有的文字种类，就算混入。

用法:
    python langkeycheck.py [源码目录]
退出码: 0 = 无问题, 1 = 有问题, 2 = 找不到 Localization.cs
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_SRC = HERE.parent / "HashChecker_WPF" / "HashChecker"

# 代码里取用语言资源的全部入口。新增取用方式时要同步加到这里，
# 否则 LK1 会漏掉那部分键。
ACCESSORS = ("Localization.Get", "Localization.Format")

# 每种语言「应该出现」的文字种类。缺少不算错（页面可能全是数字），
# 但**出现不该有的**一定报。
EXPECTED_SCRIPTS = {
    "zh-CN": {"han"},
    "zh-TW": {"han"},
    "en-US": {"latin"},
}

SCRIPT_NAMES = {
    "han": "汉字",
    "kana": "假名",
    "hangul": "谚文",
    "latin": "拉丁字母",
}


def is_han(ch):
    o = ord(ch)
    return (0x4E00 <= o <= 0x9FFF or      # CJK 统一表意文字
            0x3400 <= o <= 0x4DBF or      # 扩展 A
            0xF900 <= o <= 0xFAFF or      # 兼容表意文字
            0x3040 <= o <= 0x30FF or      # 平假名 + 片假名（下面单列）
            0xAC00 <= o <= 0xD7AF)        # 谚文


def script_of(ch):
    """判断单个字符属于哪种文字。非文字字符返回 None。"""
    o = ord(ch)
    if 0x3040 <= o <= 0x309F or 0x30A0 <= o <= 0x30FF:
        return "kana"
    if 0xAC00 <= o <= 0xD7AF or 0x1100 <= o <= 0x11FF:
        return "hangul"
    if 0x4E00 <= o <= 0x9FFF or 0x3400 <= o <= 0x4DBF or 0xF900 <= o <= 0xFAFF:
        return "han"
    if ("a" <= ch <= "z") or ("A" <= ch <= "Z"):
        return "latin"
    # 带变音符号的拉丁字母（é / ü / ñ …）
    if 0x00C0 <= o <= 0x024F:
        return "latin"
    return None


def scripts_in(text):
    """返回这段文本里出现的文字种类集合。"""
    found = set()
    for ch in text:
        s = script_of(ch)
        if s:
            found.add(s)
    return found


LANG_HEADER = re.compile(r'"((?:zh-CN|zh-TW|en-US))"\s*,\s*new Dictionary<string, string>\s*\{')
ENTRY = re.compile(r'\{\s*"(\w+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\}')


def load_languages(loc_path):
    """按语言切出各自的 key -> value。"""
    text = loc_path.read_text(encoding="utf-8", errors="replace")
    out = {}
    matches = list(LANG_HEADER.finditer(text))
    for index, m in enumerate(matches):
        start = m.end()
        depth = 1
        i = start
        while i < len(text) and depth > 0:
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
            i += 1
        segment = text[start:i - 1]
        base_line = text[:start].count("\n") + 1
        out[m.group(1)] = (dict(ENTRY.findall(segment)), segment, base_line)
    return out


def load_used_keys(src):
    """扫描代码里取用过的键，记录每处的 文件:行。"""
    pattern = re.compile(
        r'\b(?:' + "|".join(re.escape(a) for a in ACCESSORS) + r')\s*\(\s*"(\w+)"')
    used = {}
    for cs in sorted(src.rglob("*.cs")):
        if any(part in ("obj", "bin") for part in cs.parts):
            continue
        if cs.name.endswith(".Designer.cs"):
            continue
        content = cs.read_text(encoding="utf-8", errors="replace")
        for lineno, line in enumerate(content.splitlines(), 1):
            for match in pattern.finditer(line):
                used.setdefault(match.group(1), []).append("%s:%d" % (cs.name, lineno))
    return used


def main():
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_SRC
    loc = src / "Localization.cs"
    if not loc.exists():
        print("找不到 %s" % loc)
        return 2

    languages = load_languages(loc)
    if not languages:
        print("没有解析到语言段: %s" % loc)
        return 2

    used = load_used_keys(src)

    print("=" * 72)
    print("资源键完整性 + 语言纯度检查")
    print("源码目录: %s" % src)
    print("=" * 72)
    for name in sorted(languages):
        print("  %-6s %d 键" % (name, len(languages[name][0])))

    problems = []

    # ---------------- LK1 代码取用键必须存在于词典 ----------------
    print("")
    print("--- LK1 代码取用键 ⊆ 词典键 ---")
    all_keys = set()
    for name in languages:
        all_keys |= set(languages[name][0].keys())

    missing = sorted(set(used) - all_keys)
    if missing:
        for key in missing:
            where = ", ".join(used[key][:3])
            print("  !! 缺失键 %s   取用点: %s" % (key, where))
            problems.append("LK1 缺失键 %s" % key)
    else:
        print("  OK  代码取用 %d 个键，全部存在于词典" % len(used))

    # ---------------- LK2 语言纯度 ----------------
    print("")
    print("--- LK2 语言纯度（不得混入别种文字）---")
    mixed_total = 0
    for name in sorted(languages):
        entries, segment, base_line = languages[name]
        allowed = EXPECTED_SCRIPTS.get(name)

        # 逐条值检查，比整段检查精确得多：整段里出现汉字可能只是注释。
        for key in sorted(entries):
            value = entries[key]
            found = scripts_in(value)
            if not found:
                continue
            foreign = {s for s in found if s != "latin"} if name == "en-US" else \
                      ({s for s in found if s != "han"} if name.startswith("zh") else set())
            # en-US 允许汉字以外的一切？不 —— 英文段里出现汉字就是错。
            if name == "en-US":
                foreign = found & {"han", "kana", "hangul"}
            elif name.startswith("zh"):
                foreign = found & {"kana", "hangul"}
            else:
                foreign = set()

            if foreign:
                mixed_total += 1
                desc = "、".join(SCRIPT_NAMES.get(s, s) for s in sorted(foreign))
                # 在段内定位这一行，给出准确行号
                line_no = segment[:segment.find('"%s"' % key)].count("\n") + base_line if key in segment else 0
                print("  !! %s 的「%s」混入%s：%s" % (name, key, desc, value[:60]))
                print("        L%s  %s" % (line_no or "?", key))
                problems.append("LK2 %s 的 %s 混入%s" % (name, key, desc))
    if mixed_total == 0:
        print("  OK  三语文本均未混入别种文字")
    else:
        print("  共 %d 条文本混入别种文字" % mixed_total)

    # ---------------- LK3 同键形状差异 ----------------
    print("")
    print("--- LK3 同键在不同语言里的长度差异（疑似漏翻）---")
    suspicious = 0
    for key in sorted(all_keys):
        values = {}
        for name in languages:
            v = languages[name][0].get(key)
            if v is not None:
                values[name] = v
        if len(values) < 2:
            continue
        lengths = {n: len(v) for n, v in values.items()}
        shortest = min(lengths.values())
        longest = max(lengths.values())
        # 一个语言是空串、另一个有内容 —— 典型的漏翻。
        empties = [n for n, L in lengths.items() if L == 0]
        if empties and longest > 0:
            print("  !! 「%s」在 %s 里是空串，其他语言有内容" % (key, "、".join(empties)))
            problems.append("LK3 %s 在 %s 为空" % (key, "、".join(empties)))
            suspicious += 1
        # 长度差 4 倍以上（且绝对差 > 40）也算可疑，仅提示不计失败
        elif shortest > 0 and longest / float(shortest) >= 4 and longest - shortest > 40:
            print("  --   「%s」长度差较大：%s" % (
                key, "，".join("%s=%d" % (n, lengths[n]) for n in sorted(lengths))))
            suspicious += 1
    if suspicious == 0:
        print("  OK  未发现空串或长度悬殊的条目")

    print("")
    print("TOTAL PROBLEMS: %d" % len(problems))
    if problems:
        print("")
        print("摘要：")
        for p in problems[:10]:
            print("  !! %s" % p)
        if len(problems) > 10:
            print("  …… 另有 %d 条" % (len(problems) - 10))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
