#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""测试用的安全删除与临时目录管理。

背景
----
测试脚本手里握着「删目录」的权限，而路径是拼出来的。一次拼错就可能是
把源码目录、把用户的真实配置目录整个删掉 —— 这类事故没有 undo。
本项目已经两次因为路径处理不当动了不该动的东西（探针把配置写进
`%LOCALAPPDATA%`、变异测试把 `%TEMP%` 字面量当相对路径建到了工程里）。

所以这里的删除一律走白名单校验，**五条全过才删**：

1. 目标必须在**受控根目录**之内（默认 `%TEMP%`，也可显式指定别的根）；
2. 目标不能是受控根目录本身，更不能是根的上级；
3. 目标路径里必须含**标记串**（默认 `HashCheckerTest`），避免误删同名目录；
4. 目录里如果有源码类文件（.cs/.py/.xaml/.csproj/.sln/…）直接拒绝 ——
   测试产物里不该有这些东西，出现了就说明路径搞错了；
5. 任何一条不满足就抛异常，绝不"尽力删一点"。

用法
----
    from safe_ops import SafeTemp, safe_rmtree, check_deletable

    tmp = SafeTemp("regress")            # %TEMP%\HashCheckerTest_regress
    tmp.ensure()                         # 建好（已存在就清空重来）
    tmp.sub("a")                         # 开子目录
    tmp.cleanup()                        # 跑完统一清理

    safe_rmtree(some_path)               # 带白名单校验的删除
    check_deletable(p)                   # 只校验不删（写报告用）
"""
import os
import shutil
import sys
import time
from pathlib import Path

# 受控根目录：所有临时产物都必须在这些目录之下。
DEFAULT_ROOTS = [Path(os.environ.get("TEMP") or os.environ.get("TMP") or r"C:\Windows\Temp")]

# 标记串：目标路径里必须含这个才允许删。
MARKER = "HashCheckerTest"

# 源码类扩展名：目录里有这些文件就说明路径搞错了，不能删。
SOURCE_EXTS = {
    ".cs", ".py", ".xaml", ".csproj", ".sln", ".def", ".rc",
    ".md", ".json", ".resx", ".settings", ".user", ".ico", ".png", ".manifest",
}

# 这些文件名也当源码看（无扩展名或双扩展名）
SOURCE_NAMES = {"directory.build.props", "app.config", "app.manifest"}


class UnsafeDeleteError(Exception):
    """目标路径不满足删除白名单要求。"""


def _norm(p):
    """把路径规整成绝对、解析过符号链接的形态。"""
    return Path(os.path.abspath(os.path.realpath(str(p))))


def is_under(child, parent):
    """child 是否在 parent 之内（不含 parent 本身）。"""
    try:
        Path(child).relative_to(Path(parent))
        return True
    except ValueError:
        return False


def check_deletable(target, roots=None, marker=MARKER):
    """校验目标是否允许删除。不允许就抛 UnsafeDeleteError。

    返回校验通过的原因说明，方便写进报告。
    """
    roots = [_norm(r) for r in (roots or DEFAULT_ROOTS)]
    t = _norm(target)

    # 1) 必须在受控根之内
    matched = None
    for r in roots:
        if is_under(t, r):
            matched = r
            break
    if matched is None:
        raise UnsafeDeleteError(
            "目标不在受控根目录内: %s\n  受控根: %s"
            % (t, ", ".join(str(r) for r in roots)))

    # 2) 不能是根本身
    if t == matched:
        raise UnsafeDeleteError("目标是受控根目录本身, 拒绝删除: %s" % t)

    # 3) 必须含标记串
    if marker and marker.lower() not in str(t).lower():
        raise UnsafeDeleteError(
            "目标路径里没有标记串 %r, 拒绝删除: %s" % (marker, t))

    # 4) 目录里有源码类文件就拒绝
    if t.is_dir():
        bad = []
        try:
            for f in t.rglob("*"):
                if not f.is_file():
                    continue
                name = f.name.lower()
                if (f.suffix.lower() in SOURCE_EXTS
                        or name.endswith(".xaml.cs")
                        or name in SOURCE_NAMES):
                    bad.append(f)
                    if len(bad) >= 5:
                        break
        except OSError:
            pass
        if bad:
            raise UnsafeDeleteError(
                "目标目录里有源码类文件, 拒绝删除（路径可能搞错了）: %s\n  例如: %s"
                % (t, ", ".join(str(b) for b in bad[:5])))

    return "已通过白名单校验（根=%s, 标记=%s）" % (matched, marker)


def safe_rmtree(target, roots=None, marker=MARKER, ignore_errors=True, retries=3):
    """带白名单校验地删除目录。目标不存在时什么都不做。

    返回校验说明；不允许删除时抛 UnsafeDeleteError。
    """
    t = _norm(target)
    if not t.exists():
        return "目标不存在, 跳过: %s" % t
    reason = check_deletable(t, roots, marker)

    # 文件被占用（子进程刚退出、句柄还没放）时重试几次，
    # 而不是直接抛错让整个测试收尾崩掉。
    for _ in range(retries):
        try:
            shutil.rmtree(str(t), ignore_errors=ignore_errors)
        except OSError:
            pass
        if not t.exists():
            return reason + " -> 已删除"
        time.sleep(0.3)

    if t.exists():
        # 最后再试一次，这次让它把错误说清楚
        try:
            shutil.rmtree(str(t), ignore_errors=False)
            return reason + " -> 已删除"
        except OSError as e:
            return reason + " -> !! 没能删干净: %s" % e
    return reason + " -> 已删除"


def safe_unlink(target, roots=None, marker=MARKER):
    """带白名单校验地删除单个文件。"""
    t = _norm(target)
    if not t.exists():
        return "目标不存在, 跳过: %s" % t
    reason = check_deletable(t, roots, marker)
    t.unlink()
    return reason + " -> 已删除"


class SafeTemp(object):
    """受控的临时目录。

    用法:
        tmp = SafeTemp("regress")
        work = tmp.ensure()
        # ... 测试 ...
        tmp.cleanup()
    """

    def __init__(self, name, roots=None, marker=MARKER):
        self.roots = [_norm(r) for r in (roots or DEFAULT_ROOTS)]
        self.marker = marker
        self.name = name
        self.path = self.roots[0] / ("%s_%s" % (marker, name))
        self._created = False

    def ensure(self):
        """建好目录并返回它。已存在就清空重来。"""
        if self.path.exists():
            safe_rmtree(self.path, self.roots, self.marker)
        self.path.mkdir(parents=True, exist_ok=True)
        self._created = True
        return self.path

    def sub(self, name):
        """在临时目录里开一个子目录。"""
        p = self.path / name
        p.mkdir(parents=True, exist_ok=True)
        return p

    def cleanup(self):
        """清理整个临时目录。校验不通过就报告，不强删。"""
        if not self.path.exists():
            return "临时目录不存在, 跳过"
        try:
            return safe_rmtree(self.path, self.roots, self.marker)
        except UnsafeDeleteError as e:
            return "!! 清理被拒绝: %s" % e


def selftest():
    """自检：五条闸门都必须真的拦得住，且正常路径要能通过。

    一个安全工具如果自己没被验证过，出问题时你无法判断是「真的安全」
    还是「检查根本没生效」。所以把正反两侧都跑一遍。
    """
    import tempfile

    failures = []

    def expect(name, fn, should_pass):
        try:
            fn()
            got = True
            why = ""
        except (UnsafeDeleteError, ValueError) as e:
            got = False
            why = str(e).splitlines()[0][:70]
        if got != should_pass:
            failures.append("%s 期望%s，实际%s %s"
                            % (name, "通过" if should_pass else "拒绝",
                               "通过" if got else "拒绝", why))
            print("  !!  %s —— 期望%s" % (name, "通过" if should_pass else "拒绝"))
        else:
            print("  OK  %s（%s）" % (name, "通过" if should_pass else "拒绝"))

    base = Path(tempfile.gettempdir()) / (MARKER + "_selftest")
    if base.exists():
        shutil.rmtree(str(base), ignore_errors=True)
    base.mkdir(parents=True, exist_ok=True)
    (base / "data.bin").write_bytes(b"x")

    try:
        # 正面：带标记、在 %TEMP% 下、无源码 → 允许
        expect("合法临时目录", lambda: check_deletable(base), True)

        # 反面 1：不在 %TEMP% 下
        expect("工程内目录被拒", lambda: check_deletable(Path(__file__).resolve().parent), False)

        # 反面 2：没有标记串
        no_marker = Path(tempfile.gettempdir()) / "NoMarkerHere"
        no_marker.mkdir(exist_ok=True)
        expect("无标记串被拒", lambda: check_deletable(no_marker), False)

        # 反面 3：%TEMP% 根本身
        expect("受控根本身被拒", lambda: check_deletable(Path(tempfile.gettempdir())), False)

        # 反面 4：目录里有源码文件
        src = base / "sub"
        src.mkdir(exist_ok=True)
        (src / "evil.cs").write_text("// test", encoding="utf-8")
        expect("含源码文件被拒", lambda: check_deletable(base), False)
        (src / "evil.cs").unlink()
    finally:
        shutil.rmtree(str(base), ignore_errors=True)
        try:
            no_marker.rmdir()
        except OSError:
            pass

    if failures:
        print("")
        for f in failures:
            print("  !! %s" % f)
        print("自检不通过 %d 项" % len(failures))
        return 1
    print("")
    print("自检全部通过 ✓（五条闸门正反两侧均已验证）")
    return 0


if __name__ == "__main__":
    # 直接运行时只做自检：这个模块本身没有别的命令行接口。
    sys.exit(selftest())
