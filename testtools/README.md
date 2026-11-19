# HashChecker 测试工具

只做测试，不改产品代码。

被测程序：`..\HashChecker_WPF\HashChecker\bin\<Config>\HashChecker.exe`

## 一键跑测试

**最省事的做法**（推荐）：一条命令跑完全部 40 项，并给出逐项结论表。

```powershell
cd "<HashChecker 项目>\testtools"

python run_all_tests.py                 # 全部跑一遍，退出码 0 = 全通过
python run_all_tests.py --config Release # 跑 Release 构建
python run_all_tests.py --only hashprobe,uiprobe   # 只跑指定的几项
python run_all_tests.py --skip perfprobe          # 跳过耗时的
python run_all_tests.py --verbose        # 连每个工具的原始输出一起打印
python run_all_tests.py --list           # 只列出会跑哪些项
```

它自己负责：找 `bin\<config>\HashChecker.exe`、按每个工具真正需要的参数把它调起来、
把结论翻译成「通过 / 失败 / 跳过」、汇总成一张表。只读产品、只写 `%TEMP%`。

**也可以逐个跑**：

```powershell
python recordcheck.py            # 校验记录解析 / 双向反查 / 算法判定（61 项）
python localization_check.py     # 三语词典一致性（键集合 / 占位符 / 死引用）
python cs_check.py ..\HashChecker_WPF\HashChecker    # 静态检查：命名类型、文档标签、路径边界、空 catch
python namecheck.py ..\HashChecker_WPF\HashChecker   # 命名风格一致性
python regioncheck.py ..\HashChecker_WPF\HashChecker # region 结构：配对、嵌套 ≤1、跨度 ≤150 行
python castcheck.py ..\HashChecker_WPF\HashChecker   # 强制转换与控件类型：FindName(…) as T 是否恒为 null
python renamecheck.py ..\HashChecker_WPF\HashChecker # 改名残留：旧名无残留、新名定义与引用一致、名字与类型相符
python locargs.py ..\HashChecker_WPF\HashChecker     # 本地化：Format 传参个数与 {n} 占位符是否匹配
```

C# 探针（`cs\` 下）单独跑：

```powershell
.\cs\bandclip\bandclip.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\bandclip
.\cs\bandclip\bandclip.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\bandclip --section v06
.\cs\bandclip\bandclip.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\bandclip --record <多文件校验记录>
.\cs\locprobe\locprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe
.\cs\locprobe\locprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\loc --section v10
.\cs\aboutprobe\aboutprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\about
.\cs\colfit\colfit.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\colfit
.\cs\engineorder\engineorder.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\engineorder
```

`bandclip` 需要一个能出现下拉条的列表；纵向滚动条出不来时它按「环境不满足」返回 2，
不产生假结论。`--record` 可以直接喂一份真实的多文件校验记录（例如 29 项的
`ASCIIPlayer.7z.sha256`），用真实数据而不是合成文件验证。

`recordcheck.py` 自带编译依赖的探针；其余都支持直接传源码目录。

C# 探针（`cs\` 下）单独跑：

```powershell
python cs\build_all.py                          # 编译全部探针
python cs\build_all.py saveprobe                # 只编译指定一个
.\cs\hashprobe\hashprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe
.\cs\recordprobe\recordprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\recprobe
.\cs\blake3probe\blake3probe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\b3
.\cs\pickerprobe\pickerprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe
.\cs\guiprobe\guiprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\gui 398d42d5677b8bc40eb177ea45565bea3fc2fec2927c80304424c244435f9a78
.\cs\suffixprobe\suffixprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\suffix
.\cs\saveprobe\saveprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe
.\cs\addrecordprobe\addrecordprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\addrec
.\cs\defaultcheck\defaultcheck.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\defaults
.\cs\bandprobe\bandprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\band
.\cs\hangprobe\hangprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\hang
.\cs\pathprobe\pathprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\path
.\cs\settingsprobe\settingsprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\settings
.\cs\engineprobe\engineprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\engine
.\cs\writeprobe\writeprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\write
.\cs\perfprobe\perfprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\perf --quick
```

退出码一律：0 = 全过，1 = 有失败项，2 = 环境不满足（比如被测程序不存在）。

### 探针不会碰你真实的用户配置

`cs\` 下的每个探针在进入 `Main` 之后**第一件事**就是调用 `RedirectSettingsToTemp()`，
把环境变量 `HASHCHECKER_SETTINGS_DIR` 指向 `%TEMP%\HashChecker\settings-<探针名>`。
产品侧 `AppSettings.SettingsFolder` 看到这个变量就整段改道，你真正的
`%LOCALAPPDATA%\HashChecker\settings.json` 因此一次都不会被读改。

为什么需要这一层：探针用 `Assembly.LoadFrom` 载入产品程序集，产品按「入口程序集名」决定配置目录名，
而那个名字是**探针自己**。没有改道时每跑一个探针就会在 `%LOCALAPPDATA%` 下新建一个以探针命名的目录。
顺带一句：`Environment.SetEnvironmentVariable("LOCALAPPDATA", …)` 改不动这条路 ——
`GetFolderPath(SpecialFolder.LocalApplicationData)` 读的是注册表里的已知文件夹，不认这个变量。

> **这里踩过一个坑：`perfprobe` 定义了改道函数却忘了调用。**
> 它的 `RedirectSettingsToTemp()` 一直存在、也一直写在文件里，只是 `Main` 里没有那一行调用 ——
> 于是它每跑一次就在 `%LOCALAPPDATA%` 下留一个 `perfprobe\` 目录。
> 别的探针都调了，唯独它漏了，所以光看「文件里有没有这个函数」是查不出来的，
> 必须查「有没有真的被调用」。
>
> 现在的做法是：`run_all_tests.py` 每次跑完都做一次 `settings-isolation` 检查，
> 直接看真实 `%LOCALAPPDATA%` 下有没有多出以探针命名的目录 ——
> 这比逐个核对调用点可靠，因为漏一个就会当场暴露，而不是等到下次有人想起来才发现。

## 目录结构

```
testtools/
├── README.md                本文件
├── run_all_tests.py         一键跑完全部测试并汇总（推荐入口）
├── test_env.py              设置目录隔离：让探针只写 %TEMP%，不碰用户真实配置
├── safe_ops.py              安全删除：五条白名单闸门（直接运行 = 自检）
├── langkeycheck.py          资源键完整性 + 语言纯度（英/繁不得混入别种文字）
├── heuristic_probe.py       启发式探测：产品承诺 vs 实际行为（探索型）
├── cs_check.py              静态检查：命名/类型不一致、文档标签、路径边界、空 catch
├── namecheck.py             命名风格一致性：字段/属性/方法/局部变量各自风格
├── regioncheck.py           region 结构：配对、嵌套 ≤1、跨度 ≤150 行
├── renamecheck.py           改名残留：旧名无残留、新名定义与引用一致、名字与类型相符
├── localization_check.py    三语词典：键集合、{n} 占位符数量、代码引用的死键
├── recordcheck.py           校验记录：宽容解码、行格式、文件名、算法判定（调 recordprobe）
├── suffix_list.py           从 HashAlgorithms 导出首选后缀；--check 查过滤器覆盖
├── fix_filters.py           用全部首选后缀重建保存/打开对话框过滤器
├── deadcode.py              死代码：全项目只出现一次的成员（反射入口已排除）
├── castcheck.py             强制转换与控件类型：FindName(…) as T 是否会恒为 null
├── locargs.py               本地化：Format 传参个数与词典 {n} 占位符是否对得上
├── redirectcheck.py         配置改道：每个探针的 Main 里是否真的调用了改道函数
└── cs/                      需要进程内跑的 C# 探针（反射直调产品内部类型）
    ├── build_all.py         编译下面所有探针（二进制放在各自目录里）
    ├── hashprobe/           多算法内核的密码学正确性（15 个内核 × 5 类断言，含 BLAKE3 树形边界）
    ├── blake3probe/         BLAKE3 端到端：目录登记 / 记录往返 / 长度冲突 / 双向反查
    ├── pickerprobe/         算法下拉框：BLAKE3 在列、项数与目录一致、选中后能算对
    ├── guiprobe/            端到端：真窗口 + 真 .blake3 记录 → 那一行必须判定通过
    ├── recordprobe/         校验记录解析 / 反推 / 算法判定（61 项运行时断言）
    ├── suffixprobe/         算法 ↔ 后缀契约：唯一性 + 保存往返 + 冲突回退
    ├── saveprobe/           保存路径的后缀纠正（双后缀 .sha3-512.sha256 等）
    ├── saveorder/           记录写出的行序契约：按 Sequence 升序、跳过无值行、只写基名
    ├── addrecordprobe/      连续载入两份记录：算法是否更新、是否触发重算
    ├── defaultcheck/        WriteBom 默认值 / BOM 读写 / 拖入文件跟随下拉框算法
    ├── bandprobe/           框选：多选模式、矩形选行、覆盖层不拦事件
    ├── bandclip/            框选与下拉条边界：覆盖层/内容区不含下拉条、按下判定、选择增量更新
    ├── bandgesture/         框选手势：几何（运行时）+ IL（静态）双证据
    ├── colfit/              列宽自适应：右键菜单项、随窗口比例分配、最大化/恢复重排、过窄时放弃
    ├── uiprobe/             界面健康：控件名重复、TabIndex、默认/取消按钮、文本溢出、初始焦点
    ├── engineorder/         计算引擎：交付顺序、进度单调、吞吐下限、多线程结果一致
    ├── progresslife/        进度条生命周期：停止后必须清回 0，迟到回调必须丢弃
    ├── locprobe/            本地化运行时：三语词典完整性、Format 契约、程序集版本
    ├── aboutprobe/          关于窗口：版本号自动跟随程序集版本、不得显示宿主进程的 0.0.0.0
    ├── hangprobe/           界面卡死：自验证 / 计算器读文件是否派发到后台
    ├── pathprobe/           记录内路径边界：`..` 穿越 / UNC / 末分隔符 / 超长 / NUL
    ├── settingsprobe/       设置容错：坏 JSON / 缺键 / 越界值 / 转义往返
    ├── engineprobe/         计算引擎并发：线程数越界 / 取消 / 占用文件 / 多候选
    ├── writeprobe/          记录写出：BOM 开关 / 只读与占用 / Unicode 名 / 无目录
    └── perfprobe/           吞吐基准（只报数，不判失败）
```

## 默认值与算法同步

`cs\defaultcheck` 查三件事：

1. **WriteBom 默认为 false** —— 带 BOM 的校验文件在很多工具里会让第一行哈希值
   前面多出 U+FEFF 而解析失败，所以默认写纯 UTF-8；
2. **写出字节真的没 BOM** —— 不只看设置值，还要看 `ChecksumFile.Write` 的实际输出
   （首字节应为 `0x30` 之类，而不是 `EF BB BF`）；
3. **BOM 记录仍能读** —— 改了默认值不等于可以丢掉读取侧的宽容性。

同一个探针还验证「拖入文件时新行的算法跟随下拉框选择」：
把下拉框切到 SHA3-512，调 `AddPaths` 传入一个没有校验记录的普通文件，
断言该行 `SelectedAlgorithm` 与 `RequiredAlgorithms` 都是 sha3-512；
再切到 MD5 拖第二个文件，验证不是只对第一个文件生效。

它还守着「INI 迁移不许被加回来」这条：断言 `LegacyPath` 与 `ApplyLegacyIni`
已经不存在，再往设置目录里放一份 `settings.ini`，验证里面的 `Threads=3` /
`UpperCase=true` / `Algorithm=sha3-512` 一条都不被采用，且程序不会擅自删掉它。
设置只认 `settings.json`。

## 死代码扫描

```powershell
python deadcode.py ..\HashChecker_WPF\HashChecker
```

统计每个成员名在整个源码集里出现的次数，只出现一次就是「只有声明、没人调用」。

设计上刻意不做真正的语法分析：这是手写 C#，没有生成代码参与，按名字计数已经够用，
误报一眼能看出来。XAML 里 `x:Name` 声明的字段与 `Click=` / `SelectionChanged=` 等
事件处理器都算作引用，所以界面在用但代码里搜不到名字的成员不会被误判。

退出码：0 = 未发现可疑项，1 = 有可疑项。

已知的盲区：由字符串反射调用的成员会被误报。`RowsIn` 就是这种（探针按名字调它），
所以新增的反射入口要在探针里出现字面量，否则会被这个脚本列为可疑。

## 界面卡死

`cs\hangprobe` 守的是「自验证」与「计算器读文件」不许占住 UI 线程。

这两处原来都在 UI 线程上同步读文件算摘要：用 SHA3-512 算 24MB 要三秒，
窗口在这段时间不重绘、点关闭没反应——就是用户说的「卡死」。

判断方式是反汇编方法体的 IL，查里面有没有 `Task.Run` / `AsyncVoidMethodBuilder`，
并确认入口里不再残留 `FileStream` + `Append` 的同步循环。

> async 方法被编译成「外壳 + 状态机」：外壳只有 `AsyncVoidMethodBuilder.Start`，
> 真正的 `await Task.Run` 在状态机类的 `MoveNext` 里。所以探针要先看外壳，
> 再顺着 `<方法名>d__N` 找到状态机读 `MoveNext`——只看外壳会误判成「没有异步」。

为什么不用「调入口再测耗时」：自验证算完会弹 MessageBox，在无消息循环的探针进程里
那是一次模态调用，会把探针自己挡死（实测表现为 0xC0000005），
测出来的不是「卡不卡 UI」而是「探针会不会死等」。IL 判据不运行代码，稳且准确。

探针还会量一次真实代价（SHA3-512 算 24MB 约 3 秒），用来说明「如果同步会卡多久」。

## 框选多选

`ListRubberBand` 让列表支持「按住左键拖出矩形选中多行」。

设计要点（都是踩过坑才定的）：

* WPF 的 ListView **没有**橡皮筋框选，只有点选 / Ctrl / Shift；
* 这个列表的行宽铺满整列、行间距只有 1px，如果只允许「从空白处起拖」，
  用户几乎找不到能起拖的地方 —— 等于没有框选。
  所以改成「内容区里按下先记起点，移动超过 4px 阈值才接管」：
  单击与双击完全交给 ListView，只有明显拖动才变框选；
* **下拉条不在框选区域内**（2026-11-10 修）：框选矩形与覆盖层都按
  `ContentArea()` 裁剪 —— 列表尺寸减去可见的滚动条尺寸；按下点落在下拉条上时
  直接放行。否则拖动超过阈值那次 `CaptureMouse` 会把鼠标捕获从滑块上抢走，
  表现为「只能框选、下拉条拖不动」，而且框选矩形还会盖住下拉条；
* 覆盖层 `IsHitTestVisible=false`，绝不能吃掉列表的鼠标事件；
* 选择按**差集**增量增删，不再清空重建：清空会把已选中的行先退掉再选回来，
  每移动一次就触发一整轮 SelectionChanged，行数一多就卡。

`cs\bandprobe` 断言：列表是 `SelectionMode.Extended`、`RowsIn` 能按矩形选行、
覆盖层挂载成功且不拦事件、拖拽阈值存在。

`cs\bandclip` 断言下拉条边界与增量选择：覆盖层宽/高不含下拉条、`ContentArea()`
宽高与右下角都不越到下拉条上、下拉条上的按下不允许起框选而内容区内的允许、
同一矩形重复结算不再触发 SelectionChanged。`--section v06` 只跑下拉条边界，
`--section v09` 只跑横向下拉条。它需要真实布局，所以会**短暂显示**窗口（不做合成输入）。
> 真实鼠标拖拽已用 Win32 `mouse_event` 注入验证过（6 行列表拖出全部 6 行，
> 日志显示命中数随矩形增大从 2 → 6 递增）。该脚本依赖真实窗口句柄，
> 不适合放进无人值守的探针集，故未保留；真实拖框与真实点击下拉条仍需人工确认。

## 本地化运行时

`cs\locprobe` 是 `localization_check.py` 与 `locargs.py` 的**运行时**对手段，
两者互为交叉验证：

* `--section v07`：三语词典展开后键集合是否一致、有没有空文本或裸键名；
  进程启动时把 UI 语言设成 `fr-FR`（非支持语言），验证回退到 `en-US` 的设计；
* `--section v08`：`Format` 的运行时契约 —— 占位符真的被替换、少传参数按契约抛
  `FormatException`、缺失键回退成键名而不是抛异常；
* `--section v10`：程序集版本不低于 `1.3.0.0`，且文件版本 / 产品版本与程序集版本一致。

静态工具查「三种语言之间是否自洽」，本探针查「运行时展开后是否真的可用」，
两边都过才算这条维度有覆盖。



## 保存文件名后缀（对应「SHA3-512 存成 .sha3-512.sha256」那个 bug）

这里有两层原因，都必须修：

1. **过滤器没覆盖全部后缀** —— `SaveFileDialog` 的 `AddExtension` 在
   「文件名后缀不在 Filter 列表里」时会自动追加**首个**过滤器后缀。
   原来过滤器只列了 5 个后缀，选 SHA3-512 存出来就成了 `x.bin.sha3-512.sha256`。
   现在用 `suffix_list.py` 从算法目录导出全部 17 个后缀重建过滤器（`fix_filters.py`），
   并由 `suffix_list.py --check` 守住这条不变量。
2. **保存路径没有最终校验** —— 即使用户手输名字，也得保证后缀与算法一致。
   `MainWindow.NormalizeChecksumPath` 会剥掉名字尾部所有「算法后缀」再接上正确的那个，
   所以无论几层都能收敛。`cs\saveprobe` 覆盖 10 个手写用例 + 全部 17 个算法的双后缀场景。

> 关键细节：`ByExtension` 只认最后一个后缀，所以 `x.sha256.sha256` 的后缀同样是
> `.sha256`，会被误判成「已经正确」。判定「后缀正确」时必须同时确认
> 剥掉一层后剩下的名字不再以算法后缀结尾。

## 连续载入两份记录

用户行为：先拖 `.sha256` 记录（自动计算比对，**这是要保留的设计**），
再拖同一文件的 `.sha3-512` 记录，应重新按新记录校验。

`cs\addrecordprobe` 在 STA 线程里构造真实的 `MainWindow`（不显示），
反射连调两次 `AddPaths`，断言：算法更新为 sha3-512、保存值换成新记录的值、
旧的计算结果被清空（否则不会重算）、下拉框跟随记录切换。



## 算法 ↔ 后缀契约（对应「SHA3-256 存成 .sha256」那个 bug）

`cs\suffixprobe` 查三件事，缺一条那个 bug 就会复发：

1. **后缀唯一性** —— 17 个算法各有一个专属首选后缀，不与他人重复，
   且用该后缀反查必须回到同一个算法；
2. **保存 → 重新解析往返** —— 用算法算出的值写进以该算法后缀命名的记录，
   再解析回来，认到的算法与值都必须一模一样；
3. **后缀与值长度冲突** —— 比如 `.sha256` 文件里装了 MD5 值，解析必须以
   **值的长度**为准（长度是硬证据，后缀是人手起的名字，可能写错）。

```powershell
.\cs\suffixprobe\suffixprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\suffix
```

## 吞吐基准

```powershell
.\cs\perfprobe\perfprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\perf --quick
```

用反射直调产品的 `HashingEngine.HashFile`（真实热路径，不是重写一遍算法那种假测试）。
`--quick` 用 64MB 而不是 256MB，跑得更快。

参考量级（本机，64MB 单文件，`Release` 构建）：

| 算法 | 吞吐 |
|---|---|
| SHA-256 | ~1,460 MB/s（框架实现） |
| SHA-512 | ~510 MB/s（框架实现） |
| MD5 | ~500 MB/s（框架实现） |
| CRC-32 | ~380 MB/s |
| SHA3-256 | ~215 MB/s（纯 C# 位运算，Keccak 本身就很重） |
| BLAKE3 | ~95 MB/s（纯 C# 顺序实现，未做 SIMD 并行） |

> BLAKE3 的数字是**单线程、标量、无 SIMD** 的结果。官方实现靠 AVX2/AVX-512 同时算 8/16 个
> chunk 才能上到 GB/s 量级；这里是逐 chunk 顺序算的，所以与 SHA3-256 同一量级
> （比它快，因为 BLAKE3 的轮数更少）。要更快就得走 `System.Numerics` 的 SIMD 路径，
> 而 .NET Framework 4.7.2 上的 `Vector<uint>` 宽度与 JIT 支持都不足以兑现那份收益，
> 所以这里刻意停在「正确的标量实现」。


## 多算法内核（密码学正确性）

`HashChecker\Services\Cores\` 下的 23 个文件实现增量内核 `IHashCore`
（`Initialize` / `Append` / `Finish`），全部是 O(1) 内存的流式实现：

| 来源 | 算法 |
|---|---|
| .NET 框架（`FrameworkCore` 的五个子类） | MD5、SHA-1、SHA-256、SHA-384、SHA-512 |
| 自研（SHA-2 家族 64 位字） | SHA-512/224、SHA-512/256 |
| 自研（SHA-2 家族 32 位字） | SHA-224 |
| 自研（Keccak-f[1600] 海绵） | SHA3-224 / 256 / 384 / 512 |
| 自研 | BLAKE2s-256、BLAKE2b-256、BLAKE2b-512 |
| 自研（1 KiB chunk 的 Merkle 树） | BLAKE3-256 |
| 自研（位运算） | CRC-32/ISO-HDLC、CRC-64/XZ |

每个算法一个文件（`HashChecker\Services\Cores\`，共 23 个）。探针按 id 直接映射到类名，
类名对不上就会报「找不到」—— 所以「一个算法一个服务」这条不变量本身也被探针守着。
`hashprobe` 的【框架算法适配】段另外断言五个框架子类算出来的值各自等于对应框架 API 的值，
并且五个类的类型名互不相同。

```powershell
.\cs\hashprobe\hashprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe --vectors
```

**期望值不是猜的**：`--vectors` 会把算出来的值打印成 TSV，方便与 Python 交叉核对；
`hashprobe.cs` 里回填的期望值全部由 Python `hashlib`
（`sha512_224` / `sha512_256` / `sha3_*` / `blake2s` / `blake2b`）、官方的 `blake3`
包，与独立的 CRC 脚本算出。

覆盖 5 类断言：

- **已知向量**：空输入 / `"abc"` / `"123456789"` × 15 个内核（MD5 与 SHA-1 由「框架算法适配」一段单独覆盖）；
- **分块流式**：300 KB 数据按 1000 字节分块喂入，结果必须与一次性喂入相同；
- **实例复用**：同一内核算完一个输入后重置，再算下一个必须仍然正确
  （最容易踩的坑：`TransformFinalBlock` 之后状态已结束）；
- **BLAKE3 树形边界**：按官方 `test_vectors.json` 的输入（第 i 字节是 `i % 251`），
  对 0/1/2/3、63/64/65、127/128/129、1023/1024/1025、2048/2049、3072/3073、
  4096/4097、5120、6144、7168、8192 共 23 个长度逐个比参考值 ——
  这一组专门咬 chunk 边界与树形，任何「差一个字节」都会露出；
  另有 9 种长度 × 8 种分块大小的流式一致性；
- **大输入**：10 MB 数据，验证不抛异常且输出长度正确。

## BLAKE3 端到端

内核算对了不等于用户能用上。`cs\blake3probe` 查的是目录登记与记录链路：

```powershell
.\cs\blake3probe\blake3probe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\b3
```

- **目录**：`ById("blake3")` 存在、显示名 `BLAKE3`、首选后缀 `.blake3`、摘要 32 字节；
  后缀反查回 `blake3`；别名 `blake3` / `BLAKE3` / `Blake-3` / `blake-3` / `b3` 都认得出来；
- **与 BLAKE2b 不混**：两者的首选后缀必须不同 —— 后缀是读回时唯一的算法线索；
- **记录往返**：值写成 `.blake3` 再解析回来，算法与值都必须一模一样；
- **长度冲突**：`.blake3` 里装 MD5 的 32 位值 → 按长度判成 `md5`；
  **同长度不算冲突**：装 BLAKE2b-256 的值（也是 64 位）仍按后缀认 `blake3`，
  与 `.sha256` 装 SHA3-256 值的规则一致；
- **行内算法名**：`BLAKE3 (file) = ...` 认得出来；
- **双向反查**：`.blake3` 记录 → 找到 `payload.bin`；文件 → 找到那份记录。

`cs\pickerprobe` 守界面侧：算法下拉框里真的出现 `BLAKE3`（第 16 项）、
项数与算法目录一致、显示名不重复、选中后 `Id` 是 `blake3` 且能算出 `"abc"`
的正确值。内核与目录都对了但下拉框没列出来，用户一样用不上。

`cs\guiprobe` 是**端到端**的那一条：把一份真实的 `.blake3` 记录交给真实的
`MainWindow.AddPaths(paths, autoStart: true)`（拖放与命令行最终都走这里），
跑消息泵等它算完，再断言那一行判定为「通过」：

```powershell
.\cs\guiprobe\guiprobe.exe ..\HashChecker_WPF\HashChecker\bin\Release\HashChecker.exe %TEMP%\gui 398d42d5677b8bc40eb177ea45565bea3fc2fec2927c80304424c244435f9a78
```

断言 7 项：记录反推出一行、这一行是 `sample.bin` 而不是记录本身、保存值已填入、
算出当前值、当前值等于官方参考值、算法列显示 `BLAKE3`、状态为「通过」。
其余探针都是分别验证某一层，只有这里验证它们拼在一起仍然可用 ——
任何一层错位，用户看到的就是「校验失败」。

> 第三个参数是**期望值**，故意由调用方传入而不是写死在探针里：
> 探针自己算一遍再跟自己比就成了自证。这个值是官方 `blake3` 包对
> 10240 字节 `i & 0xFF` 数据算出来的。

## 校验记录：解析、反推与宽容解码

支持两个方向的校验记录查找，都不要求记录文件名与目标同名、
也不要求记录里写全扩展名（`ASCIIPlayer` 对得上 `ASCIIPlayer.exe`）：

| 方向 | 场景 | 入口 |
|---|---|---|
| 记录 → 文件 | 传入 `ASCIIPlayer.sha256`，反推出 `ASCIIPlayer.exe` 并载入期望值 | `ChecksumResolver.ResolveRecord` |
| 文件 → 记录 | 传入 `ASCIIPlayer.exe`，找同级目录的算法后缀记录 | `ChecksumResolver.FindRecordForFile` |

**算法由记录的后缀决定**（`.md5` → MD5、`.sha3-256` → SHA3-256、`.crc32` → CRC-32 …）。
后缀认不出来时按值的十六进制长度推断：只对应一个算法就直接定死，
对应多个（SHA-256 与 SHA3-256 都是 64 位）就保留全部候选，比对时任一个命中即通过 ——
这种情况误报「失败」比漏报更糟。

**找不到记录就只计算不比对**：传入本体时若同级目录没有算法后缀文件、目录里也没有
引用它的记录，就只算值、不判定通过/失败。

记录文件的编码走宽容探测（`Services/TextEncoding.cs`）：
BOM > 无 BOM UTF-16（按 NUL 分布判定，**必须先于** UTF-8 判断）> 严格 UTF-8 >
GB18030 / GBK / Big5 / Shift_JIS / EUC-KR > 兜底。BOM、零宽字符、全角空格在解码后统一清掉。

`recordcheck.py` 覆盖 61 项，全部是运行时断言：

- **编码**：UTF-8（有/无 BOM）、UTF-16 LE/BE（有/无 BOM）、UTF-32、GB18030、GBK、Big5、
  带中文文件名的 GB18030 记录；
- **行格式**：`hash *name`、`hash  name`、制表符、大写值、`sha256:` 前缀、`name = hash`、
  `SHA256 (name) = hash`、带引号、相对子目录、只有值、`hash*name`；
- **文件名**：记录少写扩展名、反向（磁盘上少写扩展名）、不同扩展名不误判；
- **算法**：后缀判算法（`.md5` / `.sha1` / `.sha256` / `.sha512-256`）、`.sum` 按长度推断、
  64 位值的多候选、行内算法名优先、同文件多份记录时选更强的那份；
- **负向**：二进制文件、普通说明文字（含 `readme.txt`）、内容不像记录的文件都不该被当成校验记录。

## 记录内路径边界

`cs\pathprobe` 守的是**安全边界**而不是解析正确性：记录文件的内容不可信，
它写什么路径都不该让程序跑到记录所在目录之外去认文件。

覆盖 7 个用例：`..` 穿越、末尾分隔符、盘符根、300 字符超长名、UNC 路径、
文件名含 NUL、绝对路径（后者按设计是允许的）。

```powershell
.\cs\pathprobe\pathprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\path
```

**已修（2026-11-05）**：这两条原来都是 FAIL，现在 7 项全绿。

* `..` 穿越：`ChecksumResolver.LocateTarget` 不再直接 `Path.Combine`，
  改走 `TryCombineInside(folder, recorded)` —— 把结果归一化后必须仍在记录目录内，
  否则当作这条记录没写文件名。绝对路径仍按记录格式的约定照收。
* 文件名含 NUL：`LocateTarget` 进来先用 `ChecksumFile.HasControlChars` 挡掉。
  不能只包 `Path.GetFileName`，因为 `Path.IsPathRooted` / `Path.Combine` /
   `Path.GetFullPath` / `File.Exists` 在含 NUL 的字符串上**都会**抛 `ArgumentException`。

两条都有反向验证：把修复代码换回旧写法，`pathprobe` 会重新报出对应的 FAIL
（见 `RUNDIR\logs\fix-reverse.txt`），所以「转绿」确实是修复生效而不是探针放过。

## 设置文件容错

`cs\settingsprobe` 守的是「设置出问题绝不能让程序起不来」：
16 种坏 JSON（空、截断、多余逗号、类型错、`NaN` 字符串…）必须被 `TryLoadJson`
一律吞掉并返回 false；越界的线程数与窗口尺寸必须被 `Normalize` 收回；
`Quote` 的转义必须能往返。

```powershell
.\cs\settingsprobe\settingsprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\settings
```

注意断言口径：`JsonParser.Parse` 对语法错误**按契约抛 `JsonException`**，
所以「不抛异常」只对 `TryLoadJson` 成立，对 `Parse` 不成立。

## 计算引擎的并发与边界

`cs\engineprobe` 覆盖线程数 0 / 负数 / 1 / 64、已取消的 token、被独占占用的文件、
空任务、纯目录任务、一行多候选算法。

```powershell
.\cs\engineprobe\engineprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\engine
```

两条容易写错的口径：

* `ToPercent(done, total)` 在 `total <= 0` 时返回 **100**（没有可处理的字节 = 已经做完），
  不是 0；写成 0 会让「只有目录」的任务进度条永远不到头。
* 引擎只把结果放进 `HashOutcome`，**写回 `CheckItem` 是窗口的事**
  （`MainWindow.ApplyOutcome`）。所以「占用文件被记为失败」要看 `HashOutcome.Error`，
  不能看 `CheckItem.Error`。

## 记录写出边界

`cs\writeprobe` 覆盖 BOM 开关、覆盖只读 / 隐藏文件、目标被占用、空行列表、
`*` 开头的文件名、Unicode 文件名、目标目录不存在。

```powershell
.\cs\writeprobe\writeprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\write
```

`ChecksumFile.Write` 的契约是「失败要抛」：占用与无目录都必须抛
`IOException` / `DirectoryNotFoundException`，让调用方弹窗提示，不能静默写半截。

## 记录写出的行序（saveorder）

`cs\saveorder` 查的是**写出顺序**，这一项此前是个空白：`suffixprobe` / `saveprobe` /
`writeprobe` 关心的全是后缀、BOM、转义，**没有一项检查行的先后顺序**。

`MainWindow.BuildChecksumLines` 用 `items.OrderBy(x => x.Sequence)` 排序后写出，
而 `Sequence` 是 `CheckItem` 构造时从静态计数器取的号（只读属性）。
正常界面操作里「加入顺序」恰好等于「Sequence 顺序」，所以把 `OrderBy` 删掉，
旧测试**一个都不会红**。本探针制造**解耦**：先按 alpha、beta、gamma 的次序构造
（于是 Sequence 是 3、4、5），再按 **gamma、alpha、beta** 的次序加入列表。
此时有排序则写出 `alpha, beta, gamma`，无排序则写出 `gamma, alpha, beta` —— 差别可见。

```powershell
.\cs\saveorder\saveorder.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\saveorder
```

五段：A 集合顺序与 Sequence 顺序解耦 · B 换一组打乱再验一次 · C 无当前值的行不得写出 ·
D 空列表不抛异常 · E 只写基名、行格式是「hash + 空格 + `*` + 基名」。

**变异保护**：删掉 `BuildChecksumLines` 的 `.OrderBy(x => x.Sequence)` 后，
A 段与 B 段各报失败（写出变成 `gamma, alpha, beta`），共 3 条 `!!`、退出码 1。

## 强制转换与控件类型

`castcheck.py` 补 `cs_check.py` ① 项的三个盲区：
`FindName(变量) as 具体类型`、`(类型)FindName("名字")`、`控件名 as 类型`。
判定按 WPF 的继承关系：派生类转基类永远安全，其余报出。

```powershell
python castcheck.py ..\HashChecker_WPF\HashChecker
```

> **2026-11-10 修**：后两种形态原来永远报不出来 —— 代码侧把
> `MainWindow.xaml.cs` 的 stem 算成了 `MainWindow.xaml`，而 XAML 侧登记的是
> `MainWindow`，控件类型查表必然落空。改成 `p.name[:-8]` 后两条规则才真正生效
> （已用注入用例验证：`(TextBox)FindName("AlgorithmPicker")` 现在会报出，
> 且对当前产品源码仍为 0 误报）。
> 第三种形态 `控件名 as 类型` 仍难有宿主：两端既非派生关系又要在编译期通过时，
> C# 会直接给 `CS0039`，所以它只对「一端是接口」这类写法有意义，本工程没有。

它**仍然看不到**的一类：`变量 as 类型`（两端都不是控件名，纯 C# 类型转换），
比如把 `candidate as HashAlgorithmInfo` 改成 `candidate as string as HashAlgorithmInfo`。
这种写法由编译器直接拒绝（`CS0039`），所以静态工具缺席不会漏到运行时；
但如果哪天改成 `as object as X` 这种能过编译的形态，本工具与 `cs_check.py` 都不会报，
需要靠代码评审。

## 界面健康（uiprobe）

`cs\uiprobe` 遍历每个窗口的可视树，找「看上去有、其实用不了」的控件。
这类问题编译期不报、现有测试也抓不到：

```
A. TabIndex 合法（无负值）
B. 对话框必须有且只有一个默认按钮、至多一个取消按钮，且两者不同源
C. 可见交互控件的启用情况（只统计，不判失败 —— 空列表时全灰是正确状态机）
D. 明确约束了宽度的 TextBlock 不得溢出（三语文案长短差异最大的地方）
E. 产品声明的 x:Name 在同一作用域内不得重复
F. 有主输入框的对话框，打开后焦点必须已经在输入区
G. 窗口标题不得为空（任务栏 / 窗口切换器的标识）
H. 可见按钮必须有内容，不能是一块空白
I. 显式设置的 TabIndex 不得重复（重复会让 Tab 顺序不确定）
```

```powershell
.\cs\uiprobe\uiprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\ui
```

> **它踩过的三个坑，都写进判定里了**：
> 1. **模板内部元素不算重复**。`MainWindow.xaml` 里 Button 与 ToggleButton 两个
>    `ControlTemplate` 各有一个 `x:Name="Body"` 的 `Border` —— 各自的模板作用域，
>    完全合法。第一版按「全窗口比名字」报了一堆假重复，现在按 `TemplateNames()`
>    把模板内部的声明整体排除。
> 2. **Auto 列与 StackPanel 里的 TextBlock 谈不上溢出**。它们的宽度由内容撑开，
>    `DesiredSize` 大于 `ActualWidth` 只是两次布局之间的瞬态。第一版报了
>    「LblThreads 需要 46px 只有 36px」这种假警，现在只判被明确约束了宽度的。
> 3. **「全部按钮禁用」不一定是缺陷**。主窗口在列表为空时禁用全部按钮是对的。
>    该项改为只输出统计值，不参与通过判定。
>
> 这三个坑都是**先跑出错报、再逐条查证是探针错而不是产品错**才改掉的 ——
> 反向验证见下：真正修掉 `CalculatorWindow` 不聚焦输入框这个缺陷后，
> 该项从 `!!` 变 `OK`；把修复改回去，该项立刻重新报错。

## 改名残留（renamecheck）

改名必须同步改掉全部引用点，否则运行时 `FindName` 拿到 null，或 XAML 绑定静默失效。
编译期不报、单元测试也不覆盖，所以做成工具。四类检查：

```
RN1  旧名残留：改名清单里的旧名字，源码里一个都不该剩
RN2  新名一致：新名字在 XAML 有 x:Name 定义、且在代码里有引用
RN3  名不副实：名字含类型词（全称或缩写）但元素类型对不上
RN4  孤儿控件名：x:Name 声明了却没有任何代码/绑定引用（只提示，不判失败）
```

```powershell
python renamecheck.py ..\HashChecker_WPF\HashChecker
```

`RENAMES` 清单当前为空 —— 本项目至今没有发生过控件改名，这是对的；
但 RN3/RN4 仍然完整生效，能守住新加控件的命名质量。

> **RN3 的缩写必须小心**：只认完整类型词（`TextBlock`）的话，一个叫
> `BtnFoo` 的 TextBlock 会整条漏过去（已实测确认）。所以缩写一起认。
> 但缩写会撞车 —— `List` 曾被当成 `ListView` 的缩写，结果 `ListMenu*`
> 那 10 个「列表右键菜单」项全部误报。现在 `List` 已剔除，`ListView` 只认
> `Lv` 与全称；`Box`/`Tb`/`Sp` 这类过泛的也一并剔除。

## 设置目录隔离（test_env + settings-isolation）

产品把设置写在 `%LOCALAPPDATA%\HashChecker\settings.json`。探针用
`Assembly.LoadFrom` 载入产品时，产品按「入口程序集名」决定配置目录名，
而那个名字是**探针自己** —— 于是每跑一个探针，`%LOCALAPPDATA%` 下就多一个
`colfit\`、`guiprobe\`、`pickerprobe\`……实测积累过 **9 个**。

现在两侧各有一道改道：

| 侧 | 做法 |
|---|---|
| 产品 | `AppSettings.SettingsFolder` 优先读 `HASHCHECKER_SETTINGS_DIR`，非空即整段改道 |
| 探针 | `Main` 第一句调 `RedirectSettingsToTemp()`，把它指向 `%TEMP%\HashChecker\settings-<探针名>` |
| Python 套件 | `test_env.isolate_settings_root(套件名)` + 每处 `subprocess` 传 `child_env()` |

**为什么不能改 `LOCALAPPDATA` 环境变量**：`GetFolderPath(SpecialFolder.LocalApplicationData)`
走 `SHGetFolderPath`，读的是注册表里的已知文件夹，与环境变量无关。已实测确认 ——
把 `LOCALAPPDATA` 指到别处，`GetFolderPath` 仍返回原路径。所以必须由产品自己开口子。

一键入口里有一项 `settings-isolation` 专门守这件事：它调
`test_env.leaked_probe_dirs()`，列出 `%LOCALAPPDATA%` 下「只有一个 settings.json」
的目录（排除产品自己的 `HashChecker`），有就判失败。

> 它的回归保护是变异 m17：把产品的改道分支置空，`colfit\` 与 `pickerprobe\`
> 目录立刻重新出现。**注意**：那两个探针自身仍然全绿 —— 说明既有测试对
> 「污染用户配置」这个维度是零覆盖的，这一项是唯一能抓到它的手段。

## 资源键完整性 + 语言纯度（langkeycheck）

`localization_check.py` 查的是**结构**：三语键集合一致、`{n}` 占位符数量匹配。
它有一个盲区：**三份词典同时写错同一个键时，它会报「一致」**。

`langkeycheck.py` 补三件结构检查看不到的事：

```
LK1  代码取用的键必须存在于词典（三语都缺时会静默回落，结构检查照样通过）
LK2  语言纯度：某一语言的文本里混入了别种文字
LK3  同键在不同语言里长度悬殊 / 出现空串（疑似漏翻）
```

```powershell
python langkeycheck.py ..\HashChecker_WPF\HashChecker
```

> **LK2 抓到了真问题**：en-US 段原有 **12 条**值里嵌着中文「摘要」，英文界面上
> 会显示 `Every file in the list already has a 摘要 value.` 这种半中半英的句子。
> 结构检查完全发现不了 —— 三份词典的键集合与占位符都是对的。
> 按项目既有用词惯例（校验记录用 checksum、单个值用 digest）全部改掉后，
> 现在 LK2 报 0。
>
> 判定方式是按 Unicode 区块识别文字种类（汉字 / 假名 / 谚文 / 拉丁），
> 不是按字符白名单 —— 后者漏一个字就整条失效。

## 安全删除（safe_ops）

测试脚本手里握着「删目录」的权限，而路径是拼出来的。一次拼错就可能是把源码目录、
把用户真实配置目录整个删掉，这类事故没有 undo。本项目已经两次因为路径处理不当
动了不该动的东西（探针把配置写进 `%LOCALAPPDATA%`、变异测试把 `%TEMP%` 字面量
当相对路径建到了工程里）。

所以删除一律走白名单，**五条全过才删**：

```
1. 目标必须在受控根目录之内（默认 %TEMP%）
2. 目标不能是受控根目录本身，更不能是根的上级
3. 目标路径里必须含标记串（默认 HashCheckerTest）
4. 目录里有源码类文件（.cs/.py/.xaml/.csproj/.sln/…）直接拒绝
5. 任何一条不满足就抛异常，绝不"尽力删一点"
```

```powershell
python safe_ops.py        # 直接运行 = 自检（五条闸门的正反两侧都跑一遍）
```

> 一个安全工具如果自己没被验证过，出问题时你无法判断是「真的安全」还是
> 「检查根本没生效」。所以 `safe_ops.py` 直接运行时跑的是自检：
> 合法临时目录要通过，工程内目录 / 无标记串 / `%TEMP%` 根本身 / 含源码文件
> 这四个反例全部要被拒。五项都过才打印「自检全部通过」。

## 启发式探测（heuristic_probe）

前面的套件都是**回归**：断言已知契约不退化，红了就是退化。
`heuristic_probe.py` 是**探索**：拿产品源码与 README 里写明的承诺，
去撞既有用例没覆盖的边界，把「看起来不对劲」的行为逐条摊出来交给人判断。
所以它的退出码 1 **不代表一定是 bug**。

覆盖 8 组、27 项：解析宽容性 / 摘要规范化 / 设置容错 / 路径边界 /
多编码 / 写出读回往返 / 引擎契约 / 本地化纯度。

```powershell
python heuristic_probe.py                 # 退出码 0 = 无异常，1 = 有可疑项需人判
python heuristic_probe.py --only path,engine
```

> **一个必须记住的坑**：判据不能建立在注释上。第一版 `engine` 那组用
> `"Stopwatch" in text and "TickCount" not in text` 判断节流方式，
> 结果把 `Stopwatch` 换成 `Environment.TickCount` 之后**仍然报 OK** ——
> 因为注释里有一大段在解释「为什么不用 TickCount」，`Stopwatch` 这个词还在。
> 现在先用 `strip_comments()` 剔掉注释再判。工具自己没验证过就等于没有。

## 本地化格式参数

`locargs.py` 补 `localization_check.py` 看不到的那一类：三种语言的占位符**数量**
一致，但代码里 `Format("Key", a)` 少传一个参数，编译期查不出、运行时抛
`FormatException`。本工具把 26 处 `Format` 调用的实参个数与词典里的 `{n}` 逐个对。

```powershell
python locargs.py ..\HashChecker_WPF\HashChecker
```

## 编译 C# 探针

二进制放在**各自的目录里**（`cs\hashprobe\hashprobe.exe` 这种），不往别处扔：

```powershell
python cs\build_all.py              # 编译全部
python cs\build_all.py recordprobe  # 只编译指定的
python cs\build_all.py --clean      # 先删旧的再编译
```

依赖 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 与
.NET Framework 4.7.2 参考程序集（`C:\Program Files (x86)\Reference Assemblies\...`）。

## 静态检查

### 空 catch 规则：可以吞，但必须写明为什么

`cs_check.py` 把**空 catch**（只有注释、没有语句的 catch 块）分成两档：

| 形态 | 处理 |
|---|---|
| `catch { /* 删不掉不影响结果 */ }` | 已说明 → 登记，不判失败 |
| `catch { }` | 未说明 → **判失败** |

它做**括号配对**，`catch` 与 `}` 分行、中间只有注释的写法也照样查得出来。
说明必须写在**花括号内部** —— 写在 `catch` 上方不算数。

规则含义是「异常可以吞，但必须让读代码的人看到你为什么吞」。
工具只能保证**有说明**，判断不了说明是否站得住 —— 语义仍靠评审。

报告里 `TOTAL PROBLEMS` 只统计**未说明**的；已说明的另列在
`--- 已登记（不计失败）---` 下，末尾给 `REGISTERED: N` 计数，
这样「存量说明被删掉」也能被看出来。

### 命名风格检查

```powershell
python namecheck.py ..\HashChecker_WPF\HashChecker
```

| 成员 | 风格 |
|---|---|
| 私有实例字段 | `_camelCase` |
| 私有静态字段（含 `static readonly`） | `_camelCase` |
| 私有常量 `const` | PascalCase |
| 属性（含 `=>` 表达式体） | PascalCase |
| 方法 | PascalCase |
| 局部变量 | camelCase |

退出码 0 = 全部一致，1 = 有不一致（逐条列出文件、成员、当前名、期望风格）。
**注意**：`private static string X => ...` 是属性不是字段，按 PascalCase 算。

### region 结构

```powershell
python regioncheck.py ..\HashChecker_WPF\HashChecker
```

三条规则：每个 `#region` 有对应 `#endregion`；不得嵌套超过一层；
单个 region 跨度建议 ≤ 150 行（超长只计数，不改退出码）。

## 本地化：三语词典一致性

```powershell
python localization_check.py
```

查三类问题：

1. **键集合不一致** —— 三种语言必须有完全相同的键。缺键会静默回退到英文，
   中文界面冒出英文句子；英文也缺就显示裸键名（`ColAlgorithm` 这种）。
2. **占位符数量不一致** —— 同一个键在三种语言里的 `{0}` `{1}` 数量必须相同，
   否则运行时 `string.Format` 抛 `FormatException`。这一条查出来过真 bug。
3. **死引用** —— 代码里 `Localization.Get/Format("X")` 引用了词典里不存在的键。

`Localization.cs` 里 `Localization.Format` 的参数是用 `params object[]` 传的，
多一个少一个都不会编译报错，所以只能靠这个脚本兜。

**它管不到的那一半**：本脚本只比较「三种语言之间」的占位符数量是否相同，
不比较「代码传了几个实参」。`Format("MsgSaved", lines.Count)` 少传一个 `path`，
三种语言都写两个 `{n}` 时它照样报「无问题」，要等运行时才抛 `FormatException`。
补这个缺口的是 `locargs.py`。

### 格式参数匹配

```powershell
python locargs.py ..\HashChecker_WPF\HashChecker
```

读三语词典里每个键的 `{n}` 个数，再数代码里每处 `Format("Key", …)` 的顶层实参个数
（做括号配对，所以嵌套的 `string.Join("；", notes)` 不会被当成两个参数）。
对不上就报出来，附文件、行号、键名、实传与需要的个数。

## 注意事项

- **控制台输出的编码跟着控制台代码页走**，不固定是 GBK：
  父 cmd 默认 936(GBK)，`chcp 65001` 就是 UTF-8。解码两种都要试。
- **探针要用反射调产品内部类型**，所以必须 `Assembly.LoadFrom` 被测 exe，
  不能把产品源码编进探针 —— 否则测的是副本，不是真正发出去的那份二进制。
- **`HashAlgorithm.Create(name)` 只认内置名**（MD5 / SHA1 / SHA256 / SHA384 / SHA512）。
  传 `"SHA3-256"` 返回 null，接着解引用就是访问冲突（进程 0xC0000005 退出，
  看起来像「探针崩了」而不是一条失败断言）。探针里一律显式构造实例。
- **重命名项目后要同步检查三处**：`csproj` 里的图标/资源路径、`sln` 里的工程名、
  以及 `AssemblyInfo.cs` 里的 `AssemblyTitle`。
- **临时目录**：`recordcheck.py` 只在 `%TEMP%\SHA256CheckerTest_recordcheck` 下建自己的
  探针工作目录，跑完即删，不碰源码目录。`hashprobe` / `recordprobe` 由调用方显式传工作目录。

