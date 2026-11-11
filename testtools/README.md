# HashChecker 测试工具

只做测试，不改产品代码。

被测程序：`..\HashChecker_WPF\HashChecker\bin\<Config>\HashChecker.exe`

## 一键跑测试

```powershell
cd "<HashChecker 项目>\testtools"

python recordcheck.py            # 校验记录解析 / 双向反查 / 算法判定（61 项）
python localization_check.py     # 三语词典一致性（键集合 / 占位符 / 死引用）
python cs_check.py ..\HashChecker_WPF\HashChecker    # 静态检查：命名类型、文档标签、路径边界、空 catch
python namecheck.py ..\HashChecker_WPF\HashChecker   # 命名风格一致性
python regioncheck.py ..\HashChecker_WPF\HashChecker # region 结构：配对、嵌套 ≤1、跨度 ≤150 行
python castcheck.py ..\HashChecker_WPF\HashChecker   # 强制转换与控件类型：FindName(…) as T 是否恒为 null
python locargs.py ..\HashChecker_WPF\HashChecker     # 本地化：Format 传参个数与 {n} 占位符是否匹配
```

C# 探针（`cs\` 下）单独跑：

```powershell
.\cs\bandclip\bandclip.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\bandclip
.\cs\bandclip\bandclip.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\bandclip --section v06
.\cs\bandclip\bandclip.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\bandclip --record <多文件校验记录>
.\cs\locprobe\locprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe
.\cs\locprobe\locprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe %TEMP%\loc --section v10
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

## 目录结构

```
testtools/
├── README.md                本文件
├── cs_check.py              静态检查：命名/类型不一致、文档标签、路径边界、空 catch
├── namecheck.py             命名风格一致性：字段/属性/方法/局部变量各自风格
├── regioncheck.py           region 结构：配对、嵌套 ≤1、跨度 ≤150 行
├── localization_check.py    三语词典：键集合、{n} 占位符数量、代码引用的死键
├── recordcheck.py           校验记录：宽容解码、行格式、文件名、算法判定（调 recordprobe）
├── suffix_list.py           从 HashAlgorithms 导出首选后缀；--check 查过滤器覆盖
├── fix_filters.py           用全部首选后缀重建保存/打开对话框过滤器
├── deadcode.py              死代码：全项目只出现一次的成员（反射入口已排除）
├── castcheck.py             强制转换与控件类型：FindName(…) as T 是否会恒为 null
├── locargs.py               本地化：Format 传参个数与词典 {n} 占位符是否对得上
└── cs/                      需要进程内跑的 C# 探针（反射直调产品内部类型）
    ├── build_all.py         编译下面所有探针（二进制放在各自目录里）
    ├── hashprobe/           多算法内核的密码学正确性（15 个算法 × 4 类断言）
    ├── recordprobe/         校验记录解析 / 反推 / 算法判定（61 项运行时断言）
    ├── suffixprobe/         算法 ↔ 后缀契约：唯一性 + 保存往返 + 冲突回退
    ├── saveprobe/           保存路径的后缀纠正（双后缀 .sha3-512.sha256 等）
    ├── addrecordprobe/      连续载入两份记录：算法是否更新、是否触发重算
    ├── defaultcheck/        WriteBom 默认值 / BOM 读写 / 拖入文件跟随下拉框算法
    ├── bandprobe/           框选：多选模式、矩形选行、覆盖层不拦事件
    ├── bandclip/            框选与下拉条边界：覆盖层/内容区不含下拉条、按下判定、选择增量更新
    ├── colfit/              列宽自适应：右键菜单项、随窗口比例分配、最大化/恢复重排、过窄时放弃
    ├── engineorder/         计算引擎：交付顺序、进度单调、吞吐下限、多线程结果一致
    ├── locprobe/            本地化运行时：三语词典完整性、Format 契约、程序集版本
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
* `--section v10`：程序集版本不低于 `1.0.1.0`，且文件版本 / 产品版本与程序集版本一致。

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

参考量级（本机，64MB 单文件）：

| 算法 | 吞吐 |
|---|---|
| MD5 | ~430 MB/s |
| CRC-32 | ~170–195 MB/s |
| SHA-256 / SHA-512 | ~78–130 MB/s（框架实现） |
| SHA3-256 | ~16 MB/s（纯 C# 位运算，Keccak 本身就很重） |


## 多算法内核（密码学正确性）

`HashChecker\Services\HashCores.cs` / `HashCores2.cs` / `HashCores3.cs` 实现增量内核 `IHashCore`
（`Initialize` / `Append` / `Finish`），全部是 O(1) 内存的流式实现：

| 来源 | 算法 |
|---|---|
| .NET 框架（`BuiltInCore` 适配） | MD5、SHA-1、SHA-256、SHA-384、SHA-512 |
| 自研（SHA-2 家族 64 位字） | SHA-512/224、SHA-512/256 |
| 自研（SHA-2 家族 32 位字） | SHA-224 |
| 自研（Keccak-f[1600] 海绵） | SHA3-224 / 256 / 384 / 512 |
| 自研 | BLAKE2s-256、BLAKE2b-256 |
| 自研（位运算） | CRC-32/ISO-HDLC、CRC-64/XZ |

```powershell
.\cs\hashprobe\hashprobe.exe ..\HashChecker_WPF\HashChecker\bin\Debug\HashChecker.exe --vectors
```

**期望值不是猜的**：`--vectors` 会把算出来的值打印成 TSV，方便与 Python 交叉核对；
`hashprobe.cs` 里回填的期望值全部由 Python `hashlib`
（`sha512_224` / `sha512_256` / `sha3_*` / `blake2s` / `blake2b`）与独立的 CRC 脚本算出。

覆盖 4 类断言：

- **已知向量**：空输入 / `"abc"` / `"123456789"` × 15 个算法；
- **分块流式**：300 KB 数据按 1000 字节分块喂入，结果必须与一次性喂入相同；
- **实例复用**：同一内核算完一个输入后重置，再算下一个必须仍然正确
  （最容易踩的坑：`TransformFinalBlock` 之后状态已结束）；
- **大输入**：10 MB 数据，验证不抛异常且输出长度正确。

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

