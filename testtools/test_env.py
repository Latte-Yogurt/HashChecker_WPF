#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""测试数据目录隔离：让被测程序与全部探针只写 %TEMP%，一个字节都不进用户目录。

为什么需要（这是本项目真实踩过的坑）
--------------------------------------
产品把设置写在 `%LOCALAPPDATA%\HashChecker\settings.json`。探针用
`Assembly.LoadFrom` 载入产品时，产品按「入口程序集名」决定配置目录名，
而那个名字是**探针自己** —— 于是每跑一个探针，`%LOCALAPPDATA%` 下就多一个
`colfit\`、`guiprobe\`、`pickerprobe\`……实测积累过 9 个。

修法是两侧各改一处（已在 1.3.0 落地）：
  * 产品：`AppSettings.SettingsFolder` 优先读 `HASHCHECKER_SETTINGS_DIR`；
  * 探针：`Main` 第一句调 `RedirectSettingsToTemp()`。

本模块把「探针侧该怎么改道」这件事收敛到一个地方，供 Python 套件复用，
不再让每个脚本各写一份路径拼接。

为什么不能改 `LOCALAPPDATA` 环境变量
-------------------------------------
`Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` 走的是
`SHGetFolderPath`，读用户配置文件里的已知文件夹，**与环境变量无关**。
已实测确认：把 `LOCALAPPDATA` 指到别处，`GetFolderPath` 仍返回原路径。
所以必须由产品自己提供改道点（`HASHCHECKER_SETTINGS_DIR`），测试再把它指到临时目录。

用法
----
    from test_env import isolate_settings_root, child_env

    DATA_ROOT = isolate_settings_root("runall")      # 套件入口第一件事
    ...
    subprocess.run([...], env=child_env("runall"))   # 每一处启动子进程都传它
"""
import os
import shutil
from pathlib import Path

# 与产品 AppSettings.SettingsFolder 读取的变量名保持一致。
# 这里各自写一份字面量而不是去产品里引用：本模块要能独立运行。
SETTINGS_ROOT_ENV = "HASHCHECKER_SETTINGS_DIR"

# 重置时的删除边界：只认 %TEMP% 下带这个标记的那一棵树。
MARKER = "HashCheckerTest_settings"


def temp_root():
    """%TEMP% 的 Path 形式。环境变量缺失时退到一个明确的临时位置。"""
    return Path(os.environ.get("TEMP") or os.environ.get("TMP") or r"C:\Windows\Temp")


def isolated_root(suite):
    """算出（不创建）某个套件的隔离设置根目录。"""
    return temp_root() / MARKER / str(suite)


def _assert_safe(path):
    """删除前确认目标确实是受控临时目录：在 %TEMP% 之下、且路径带标记串。

    两道都要过。只判其中一道的话，一个手滑的 `isolate_settings_root("..")`
    就能把 %TEMP% 之外的东西删掉。
    """
    target = path.resolve()
    temp = temp_root().resolve()
    if not str(target).lower().startswith(str(temp).lower() + os.sep):
        raise RuntimeError("拒绝删除 %%TEMP%% 之外的目录: %s" % target)
    if MARKER not in target.parts:
        raise RuntimeError("拒绝删除不带标记串的目录: %s" % target)


def isolate_settings_root(suite, reset=True):
    """把设置目录改道到 %TEMP%\\HashCheckerTest_settings\\<suite>，并设置环境变量。

    reset=True（默认）时先清掉上一轮遗留内容：套件之间不共享状态，
    但**同一个套件内部**的用例仍能读到彼此写出的设置。

    返回该套件的设置根目录（已创建）。
    """
    if not suite or suite.strip() in (".", "..") or os.sep in str(suite) or "/" in str(suite):
        raise ValueError("suite 必须是简单的目录名: %r" % (suite,))

    root = isolated_root(suite)
    if reset and root.exists():
        _assert_safe(root)
        shutil.rmtree(str(root), ignore_errors=True)

    root.mkdir(parents=True, exist_ok=True)
    os.environ[SETTINGS_ROOT_ENV] = str(root)
    return root


def child_env(suite=None, base=None):
    """给子进程用的 env 副本：保证带上改道变量。

    每一处 subprocess 调用都要传它。漏传一处，那个用例就会写进用户真实配置目录 ——
    这类漏传没有任何测试能发现，只能靠逐个核对。
    """
    env = dict(base) if base is not None else os.environ.copy()
    if env.get(SETTINGS_ROOT_ENV):
        return env

    if suite:
        root = isolated_root(suite)
        root.mkdir(parents=True, exist_ok=True)
        env[SETTINGS_ROOT_ENV] = str(root)
    return env


def leaked_probe_dirs(localappdata=None, real_name="HashChecker", known_probes=None):
    """列出 %LOCALAPPDATA% 下疑似由探针创建的目录。

    这是「改道到底有没有生效」的直接证据：修复后应当返回空列表。
    只做只读判断，不删任何东西。

    判据（本次加严）
    ----------------
    旧判据是「目录里**恰好只有**一个 settings.json」。它有个真实漏洞：
    探针目录里只要多出任何一个文件（日志、临时文件、子目录），就会被漏检。
    实测：构造 `hashprobe/`（settings.json + probe.log）与 `guiprobe/`（只有 settings.json），
    旧判据只检出 guiprobe，漏掉 hashprobe。

    新判据分两档，**只会检出更多，不会检出更少**（加严，不是放宽）：

      A. 强证据：目录里有 settings.json，且目录名是 `known_probes` 里的已知探针名
         —— 无论旁边还有多少文件，都算泄漏。
      B. 弱证据（仅在未提供 known_probes 时生效，保持本模块可独立使用）：
         目录里只有 settings.json 一个条目，按旧判据处理。

    必须排除两类误报：
      * `real_name`（默认 HashChecker）：用户**真实**的配置目录，
        它同样只有一个 settings.json，但它本来就是对的；
      * 任何名字不在 `known_probes` 里的目录（给了该集合时），
        免得把别家软件的目录算进来。
    """
    base = Path(localappdata) if localappdata else Path(
        os.environ.get("LOCALAPPDATA") or (Path.home() / "AppData" / "Local"))
    if not base.is_dir():
        return []

    leaked = []
    for child in sorted(base.iterdir()):
        if not child.is_dir():
            continue
        if child.name.lower() == real_name.lower():
            continue
        if known_probes is not None and child.name not in known_probes:
            continue
        try:
            entries = list(child.iterdir())
        except OSError:
            continue

        has_settings = any(e.is_file() and e.name == "settings.json" for e in entries)
        if not has_settings:
            continue

        if known_probes is not None:
            # A. 强证据：已知探针名 + 有 settings.json，旁边有多少文件都不影响判定
            leaked.append(child)
            continue

        # B. 弱证据：没有已知探针名单时，退回「只有一个 settings.json」的旧判据，
        #    避免在没有上下文的情况下把用户自己建的目录算成泄漏。
        if len(entries) == 1:
            leaked.append(child)

    return leaked
