using System.Collections.Generic;
using System.Globalization;

namespace HashChecker
{
    /// <summary>
    /// 界面文本本地化。跟随系统 UI 语言，支持 zh-CN / zh-TW / en-US，
    /// 其余语言一律回退到 en-US，避免界面出现空字符串。
    /// </summary>
    public static class Localization
    {
        private static readonly string _currentLanguage = Get_currentLanguage();

        private static readonly Dictionary<string, Dictionary<string, string>> Texts =
            new Dictionary<string, Dictionary<string, string>>
        {
            {
                "zh-CN", new Dictionary<string, string>
                {
                    { "Title",                 "HashChecker" },

                    { "MenuFile",              "文件" },
                    { "MenuEdit",              "编辑" },
                    { "MenuView",              "查看" },
                    { "MenuTools",             "工具" },
                    { "MenuHelp",              "帮助" },

                    { "FileSave",              "保存" },
                    { "FileSaveItem",          "保存单项" },
                    { "FileSaveAs",            "保存到..." },
                    { "FileExit",              "退出" },

                    { "EditAdd",               "添加/打开..." },
                    { "EditRemove",            "移除" },
                    { "EditClear",             "清空" },
                    { "EditStart",             "开始" },
                    { "EditStop",              "停止" },
                    { "EditCompare",           "比较哈希值" },
                    { "EditCopy",              "复制哈希值" },
                    { "EditCopyPath",          "复制路径" },
                    { "EditSelectAll",         "全部选定" },

                    { "ViewAll",               "全部" },
                    { "ViewUnavailable",       "不可用" },
                    { "ViewUnknown",           "未知" },
                    { "ViewLoaded",            "载入" },
                    { "ViewNew",               "新增" },
                    { "ViewFailed",            "失败" },
                    { "ViewPassed",            "通过" },
                    { "ViewLocate",            "定位" },
                    { "ViewProperties",        "属性" },

                    { "ToolsSelfTest",         "自验证" },
                    { "ToolsCalculator",       "摘要计算器..." },
                    { "ToolsOptions",          "选项..." },

                    { "HelpAbout",             "关于 HashChecker..." },

                    { "BtnSave",               "保存" },
                    { "BtnSaveItem",           "保存单项" },
                    { "BtnSaveAs",             "保存到" },
                    { "BtnAdd",                "添加" },
                    { "BtnRemove",             "移除" },
                    { "BtnClear",              "清空" },
                    { "BtnStart",              "开始" },
                    { "BtnStop",               "停止" },
                    { "BtnCopy",               "复制哈希值" },
                    { "BtnLocate",             "定位" },
                    { "BtnOptions",            "选项" },
                    { "BtnSelfTest",           "自验证" },

                    { "FilterAll",             "全部" },
                    { "FilterUnavailable",     "不可用" },
                    { "FilterUnknown",         "未知" },
                    { "FilterLoaded",          "载入" },
                    { "FilterNew",             "新增" },
                    { "FilterFailed",          "失败" },
                    { "FilterPassed",          "通过" },
                    { "FilterCount",           "{0}({1})" },

                    { "ColName",               "名称" },
                    { "ColAlgorithm",          "算法" },
                    { "ColFolder",             "所在文件夹" },
                    { "ColCurrent",            "当前值" },
                    { "ColSaved",              "保存值" },
                    { "ColSize",               "大小" },
                    { "ColModified",           "修改日期" },

                    { "StatusUnavailable",     "不可用" },
                    { "StatusUnknown",         "未知" },
                    { "StatusLoaded",          "载入" },
                    { "StatusNew",             "新增" },
                    { "StatusFailed",          "失败" },
                    { "StatusPassed",          "通过" },

                    { "StReady",               "就绪" },
                    { "StObjects",             "{0} 个对象" },
                    { "StSelected",            "选定 {0} 项" },
                    { "StHashing",             "正在计算：{0}%" },
                    { "StDone",                "计算完成" },
                    { "StStopped",             "已取消" },
                    { "StListFile",            "校验文件：{0}" },

                    { "MsgErrorTitle",         "错误" },
                    { "MsgWarningTitle",       "警告" },
                    { "MsgNoticeTitle",        "提示" },
                    { "MsgDropHint",           "把文件、文件夹或校验记录（.md5 / .sha256 / .sha3-256 …）拖到这里，或点击工具栏的「添加」按钮。" },
                    { "MsgNoHashingTargets",   "列表是空的，没有需要计算的文件。" },
                    { "MsgNoPendingTargets",   "列表中的文件都已经计算过摘要值，无需重复计算。" },
                    { "MsgHashRunning",        "正在计算，请先「停止」再修改列表。" },
                    { "MsgFileNotFound",       "文件不存在：{0}" },
                    { "MsgSaved",              "已保存 {0} 条摘要值到：\n{1}" },
                    { "MsgNothingToSave",      "列表中没有已计算摘要值的项。" },
                    { "MsgSaveFailed",         "保存失败：{0}" },
                    { "MsgNoSelection",        "请至少选择一项。" },
                    { "MsgClearConfirm",       "确定要清空整个列表（{0} 项）吗？" },
                    { "MsgCopied",             "已复制到剪贴板。" },
                    { "MsgCopyEmpty",          "没有可复制的内容。" },
                    { "MsgNoHash",             "选中项还没有摘要值，请先执行「开始」。" },
                    { "MsgOverwrite",          "文件已存在：\n{0}\n\n是否覆盖？" },
                    { "MsgSaveListFilter",     "校验记录 (*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt)|*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*" },
                    { "MsgFileFilter",         "校验记录 (*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt)|*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*" },
                    { "MsgSelfTestTitle",      "自验证：本程序自身的摘要值" },
                    { "MsgSelfTestBody",       "算法：{0}\n\n程序路径：{1}\n\n摘要值：\n{2}" },
                    { "MsgPropertiesBody",     "路径：{0}\n\n大小：{1} 字节（{2}）\n修改日期：{3}\n算法：{4}\n\n当前值：\n{5}\n\n保存值：\n{6}" },
                    { "MsgNoValue",            "（无）" },
                    { "MsgThreadsHint",        "可用范围 1 - {0}。数值越大通常越快，但机械硬盘上过大反而会因随机寻道变慢。" },
                    { "MsgRecordLoaded",       "已从校验记录匹配 {0} 项：{1}（编码：{2}）。" },
                    { "MsgRecordPartial",      "另有 {0} 条记录在磁盘上找不到对应文件。" },
                    { "MsgRecordEmpty",        "校验记录里没有任何可解析的摘要值。" },
                    { "MsgRecordNoTarget",     "校验记录里的 {0} 条记录都找不到对应文件（文件名可以不带扩展名）。" },
                    { "MsgNotAChecksumFile",   "这个文件里没有可解析的摘要值，也不是校验记录。" },

                    { "OptTitle",              "选项" },
                    { "OptThreads",            "线程数" },
                    { "OptUppercase",          "以大写形式显示与保存摘要值" },
                    { "OptAutoStart",          "添加文件后自动开始计算（默认关闭）" },
                    { "OptAutoLoadSidecar",    "自动载入同目录的校验记录（不要求同名或带扩展名）" },
                    { "OptBom",                "校验文件使用 UTF-8 BOM" },
                    { "OptRememberWindow",     "记住窗口位置与大小" },
                    { "OptOk",                 "确定" },
                    { "OptCancel",             "取消" },

                    { "CompareTitle",          "比较哈希值" },
                    { "CompareFileLabel",      "文件：" },
                    { "CompareCurrentLabel",   "当前值：" },
                    { "CompareExpectedLabel",  "保存值：" },
                    { "CompareInputLabel",     "要比较的值：" },
                    { "CompareResultMatch",    "通过：摘要值一致。" },
                    { "CompareResultMismatch", "失败：摘要值不一致。" },
                    { "CompareInvalid",          "这个值不像任何已支持算法的摘要，请检查是否粘贴完整。" },
                    { "CompareResultPending",  "请输入摘要值（MD5 / SHA-1 / SHA-2 / SHA-3 / BLAKE2 / CRC 任一）进行比对。" },
                    { "CompareOk",             "确定" },
                    { "CompareCancel",         "取消" },
                    { "ComparePaste",          "粘贴" },

                    { "CalcTitle",             "摘要计算器" },
                    { "CalcTextLabel",         "文本（按 UTF-8 编码）：" },
                    { "CalcResultLabel",       "摘要值：" },
                    { "CalcFromFile",          "从文件计算..." },
                    { "CalcCompute",           "计算" },
                    { "CalcCopy",              "复制" },
                    { "CalcClose",             "关闭" },
                    { "CalcHint",              "输入文本即实时计算；也可以选择一个文件计算它的摘要值。" },

                    { "AboutTitle",            "关于 HashChecker" },
                    { "AboutBody",              "HashChecker\n\n版本 {0}\n\n一个多算法摘要校验工具。\n支持 MD5 / SHA-1 / SHA-2 全系 / SHA-3 全系 / BLAKE2 / CRC 的批量校验、\n校验记录读写、双向反查与结果过滤复制。" },

                    { "LblAlgorithm", "算法" },
                    { "TipAlgorithmFromRecord", "算法由校验记录的文件后缀判定：{0}。" },
                    { "TipAlgorithmAmbiguous", "值长度对应 {0} 种算法，任一种匹配即算通过。" },
                    { "TipAlgorithmSelected", "没有校验记录，按当前选择的算法计算：{0}。" },
                    { "MsgAlgorithmChanged", "已切换算法：{0}。没有校验记录的行会重新计算。" },
                    { "MsgRecordAlgorithm", "记录算法：{0}。" },
                    { "MsgNoRecordHint", "同级目录下没有找到该文件的校验记录，只计算不比对。" },
                    { "OptAlgorithm", "默认算法（没有校验记录时使用）" },
                    { "AboutOk",               "确定" },
                }
            },
            {
                "zh-TW", new Dictionary<string, string>
                {
                    { "Title",                 "HashChecker" },

                    { "MenuFile",              "檔案" },
                    { "MenuEdit",              "編輯" },
                    { "MenuView",              "檢視" },
                    { "MenuTools",             "工具" },
                    { "MenuHelp",              "說明" },

                    { "FileSave",              "儲存" },
                    { "FileSaveItem",          "儲存單項" },
                    { "FileSaveAs",            "另存新檔..." },
                    { "FileExit",              "結束" },

                    { "EditAdd",               "新增/開啟..." },
                    { "EditRemove",            "移除" },
                    { "EditClear",             "清空" },
                    { "EditStart",             "開始" },
                    { "EditStop",              "停止" },
                    { "EditCompare",           "比較雜湊值" },
                    { "EditCopy",              "複製雜湊值" },
                    { "EditCopyPath",          "複製路徑" },
                    { "EditSelectAll",         "全選" },

                    { "ViewAll",               "全部" },
                    { "ViewUnavailable",       "不可用" },
                    { "ViewUnknown",           "未知" },
                    { "ViewLoaded",            "載入" },
                    { "ViewNew",               "新增" },
                    { "ViewFailed",            "失敗" },
                    { "ViewPassed",            "通過" },
                    { "ViewLocate",            "定位" },
                    { "ViewProperties",        "屬性" },

                    { "ToolsSelfTest",         "自驗證" },
                    { "ToolsCalculator",       "摘要計算器..." },
                    { "ToolsOptions",          "選項..." },

                    { "HelpAbout",             "關於 HashChecker..." },

                    { "BtnSave",               "儲存" },
                    { "BtnSaveItem",           "儲存單項" },
                    { "BtnSaveAs",             "另存" },
                    { "BtnAdd",                "新增" },
                    { "BtnRemove",             "移除" },
                    { "BtnClear",              "清空" },
                    { "BtnStart",              "開始" },
                    { "BtnStop",               "停止" },
                    { "BtnCopy",               "複製雜湊值" },
                    { "BtnLocate",             "定位" },
                    { "BtnOptions",            "選項" },
                    { "BtnSelfTest",           "自驗證" },

                    { "FilterAll",             "全部" },
                    { "FilterUnavailable",     "不可用" },
                    { "FilterUnknown",         "未知" },
                    { "FilterLoaded",          "載入" },
                    { "FilterNew",             "新增" },
                    { "FilterFailed",          "失敗" },
                    { "FilterPassed",          "通過" },
                    { "FilterCount",           "{0}({1})" },

                    { "ColName",               "名稱" },
                    { "ColFolder",             "所在資料夾" },
                    { "ColCurrent",            "目前值" },
                    { "ColSaved",              "儲存值" },
                    { "ColSize",               "大小" },
                    { "ColModified",           "修改日期" },

                    { "StatusUnavailable",     "不可用" },
                    { "StatusUnknown",         "未知" },
                    { "StatusLoaded",          "載入" },
                    { "StatusNew",             "新增" },
                    { "StatusFailed",          "失敗" },
                    { "StatusPassed",          "通過" },

                    { "StReady",               "就緒" },
                    { "StObjects",             "{0} 個物件" },
                    { "StSelected",            "選取 {0} 項" },
                    { "StHashing",             "正在計算：{0}%" },
                    { "StDone",                "計算完成" },
                    { "StStopped",             "已取消" },
                    { "StListFile",            "校驗檔：{0}" },

                    { "MsgErrorTitle",         "錯誤" },
                    { "MsgWarningTitle",       "警告" },
                    { "MsgNoticeTitle",        "提示" },
                    { "MsgDropHint",           "把檔案、資料夾或校驗記錄（.md5 / .sha256 / .sha3-256 …）拖到這裡，或點擊工具列的「新增」按鈕。" },
                    { "MsgNoHashingTargets",   "清單是空的，沒有需要計算的檔案。" },
                    { "MsgNoPendingTargets",   "清單中的檔案都已經計算過 摘要值，無需重複計算。" },
                    { "MsgHashRunning",        "正在計算，請先「停止」再修改清單。" },
                    { "MsgFileNotFound",       "檔案不存在：{0}" },
                    { "MsgSaved",              "已儲存 {0} 筆 摘要值到：\n{1}" },
                    { "MsgNothingToSave",      "清單中沒有已計算 摘要值的項目。" },
                    { "MsgSaveFailed",         "儲存失敗：{0}" },
                    { "MsgNoSelection",        "請至少選取一項。" },
                    { "MsgClearConfirm",       "確定要清空整個清單（{0} 項）嗎？" },
                    { "MsgCopied",             "已複製到剪貼簿。" },
                    { "MsgCopyEmpty",          "沒有可複製的內容。" },
                    { "MsgNoHash",             "選取項還沒有 摘要值，請先執行「開始」。" },
                    { "MsgOverwrite",          "檔案已存在：\n{0}\n\n是否覆蓋？" },
                    { "MsgSaveListFilter",     "校驗記錄 (*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt)|*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt|文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*" },
                    { "MsgFileFilter",         "校驗記錄 (*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt)|*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt|文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*" },
                    { "MsgSelfTestTitle",      "自驗證：本程式自身的 摘要值" },
                    { "MsgSelfTestBody",       "演算法：{0}\n\n程式路徑：{1}\n\n摘要值：\n{2}" },
                    { "MsgPropertiesBody",     "路徑：{0}\n\n大小：{1} 位元組（{2}）\n修改日期：{3}\n演算法：{4}\n\n目前值：\n{5}\n\n儲存值：\n{6}" },
                    { "MsgNoValue",            "（無）" },
                    { "MsgThreadsHint",        "可用範圍 1 - {0}。數值越大通常越快，但機械硬碟上過大反而會因隨機尋道變慢。" },
                    { "MsgRecordLoaded",       "已從校驗記錄比對 {0} 項：{1}（編碼：{2}）。" },
                    { "MsgRecordPartial",      "另有 {0} 筆記錄在磁碟上找不到對應檔案。" },
                    { "MsgRecordEmpty",        "校驗記錄裡沒有任何可解析的 摘要值。" },
                    { "MsgRecordNoTarget",     "校驗記錄裡的 {0} 筆記錄都找不到對應檔案（檔名可以不含副檔名）。" },
                    { "MsgNotAChecksumFile",   "這個檔案裡沒有可解析的 摘要值，也不是校驗記錄。" },

                    { "OptTitle",              "選項" },
                    { "OptThreads",            "執行緒數" },
                    { "OptUppercase",          "以大寫形式顯示與儲存 摘要值" },
                    { "OptAutoStart",          "新增檔案後自動開始計算（預設關閉）" },
                    { "OptAutoLoadSidecar",    "自動載入同目錄的校驗記錄（不要求同名或含副檔名）" },
                    { "OptBom",                "校驗檔使用 UTF-8 BOM" },
                    { "OptRememberWindow",     "記住視窗位置與大小" },
                    { "OptOk",                 "確定" },
                    { "OptCancel",             "取消" },

                    { "CompareTitle",          "比較雜湊值" },
                    { "CompareFileLabel",      "檔案：" },
                    { "CompareCurrentLabel",   "目前值：" },
                    { "CompareExpectedLabel",  "儲存值：" },
                    { "CompareInputLabel",     "要比較的 摘要值：" },
                    { "CompareResultMatch",    "通過：摘要值一致。" },
                    { "CompareResultMismatch", "失敗：摘要值不一致。" },
                    { "CompareInvalid",          "這個值不像任何已支援演算法的摘要，請檢查是否貼上完整。" },
                    { "CompareResultPending",  "請輸入摘要值（MD5 / SHA-1 / SHA-2 / SHA-3 / BLAKE2 / CRC 任一）進行比對。" },
                    { "CompareOk",             "確定" },
                    { "CompareCancel",         "取消" },
                    { "ComparePaste",          "貼上" },

                    { "CalcTitle",             "摘要計算器" },
                    { "CalcTextLabel",         "文字（依 UTF-8 編碼）：" },
                    { "CalcResultLabel",       "摘要值：" },
                    { "CalcFromFile",          "從檔案計算..." },
                    { "CalcCompute",           "計算" },
                    { "CalcCopy",              "複製" },
                    { "CalcClose",             "關閉" },
                    { "CalcHint",              "輸入文字即時計算；也可以選擇一個檔案計算它的 摘要值。" },

                    { "AboutTitle",            "關於 HashChecker" },
                    { "AboutBody",             "HashChecker\n\n版本 {0}\n\n一個多演算法摘要校驗工具。\n支援 MD5 / SHA-1 / SHA-2 全系 / SHA-3 全系 / BLAKE2 / CRC 的批次校驗、\n校驗記錄讀寫、雙向反查與結果篩選複製。" },

                    { "ColAlgorithm", "演算法" },
                    { "LblAlgorithm", "演算法" },
                    { "TipAlgorithmFromRecord", "演算法由校驗記錄的副檔名判定：{0}。" },
                    { "TipAlgorithmAmbiguous", "值長度對應 {0} 種演算法，任一種相符即算通過。" },
                    { "TipAlgorithmSelected", "沒有校驗記錄，依目前選擇的演算法計算：{0}。" },
                    { "MsgAlgorithmChanged", "已切換演算法：{0}。沒有校驗記錄的列會重新計算。" },
                    { "MsgRecordAlgorithm", "記錄演算法：{0}。" },
                    { "MsgNoRecordHint", "同層目錄下沒有找到該檔案的校驗記錄，只計算不比對。" },
                    { "OptAlgorithm", "預設演算法（沒有校驗記錄時使用）" },
                    { "AboutOk",               "確定" },
                }
            },
            {
                "en-US", new Dictionary<string, string>
                {
                    { "Title",                 "HashChecker" },

                    { "MenuFile",              "File" },
                    { "MenuEdit",              "Edit" },
                    { "MenuView",              "View" },
                    { "MenuTools",             "Tools" },
                    { "MenuHelp",              "Help" },

                    { "FileSave",              "Save" },
                    { "FileSaveItem",          "Save Item" },
                    { "FileSaveAs",            "Save To..." },
                    { "FileExit",              "Exit" },

                    { "EditAdd",               "Add/Open..." },
                    { "EditRemove",            "Remove" },
                    { "EditClear",             "Clear" },
                    { "EditStart",             "Start" },
                    { "EditStop",              "Stop" },
                    { "EditCompare",           "Compare Hash Value" },
                    { "EditCopy",              "Copy Hash" },
                    { "EditCopyPath",          "Copy Path" },
                    { "EditSelectAll",         "Select All" },

                    { "ViewAll",               "All" },
                    { "ViewUnavailable",       "Unavailable" },
                    { "ViewUnknown",           "Unknown" },
                    { "ViewLoaded",            "Loaded" },
                    { "ViewNew",               "New" },
                    { "ViewFailed",            "Failed" },
                    { "ViewPassed",            "Passed" },
                    { "ViewLocate",            "Locate" },
                    { "ViewProperties",        "Properties" },

                    { "ToolsSelfTest",         "Self Test" },
                    { "ToolsCalculator",       "Digest Calculator..." },
                    { "ToolsOptions",          "Options..." },

                    { "HelpAbout",             "About HashChecker..." },

                    { "BtnSave",               "Save" },
                    { "BtnSaveItem",           "Save Item" },
                    { "BtnSaveAs",             "Save To" },
                    { "BtnAdd",                "Add" },
                    { "BtnRemove",             "Remove" },
                    { "BtnClear",              "Clear" },
                    { "BtnStart",              "Start" },
                    { "BtnStop",               "Stop" },
                    { "BtnCopy",               "Copy Hash" },
                    { "BtnLocate",             "Locate" },
                    { "BtnOptions",            "Options" },
                    { "BtnSelfTest",           "Self Test" },

                    { "FilterAll",             "All" },
                    { "FilterUnavailable",     "Unavailable" },
                    { "FilterUnknown",         "Unknown" },
                    { "FilterLoaded",          "Loaded" },
                    { "FilterNew",             "New" },
                    { "FilterFailed",          "Failed" },
                    { "FilterPassed",          "Passed" },
                    { "FilterCount",           "{0}({1})" },

                    { "ColName",               "Name" },
                    { "ColFolder",             "Folder" },
                    { "ColCurrent",            "Current value" },
                    { "ColSaved",              "Saved value" },
                    { "ColSize",               "Size" },
                    { "ColModified",           "Modified" },

                    { "StatusUnavailable",     "Unavailable" },
                    { "StatusUnknown",         "Unknown" },
                    { "StatusLoaded",          "Loaded" },
                    { "StatusNew",             "New" },
                    { "StatusFailed",          "Failed" },
                    { "StatusPassed",          "Passed" },

                    { "StReady",               "Ready" },
                    { "StObjects",             "{0} object(s)" },
                    { "StSelected",            "{0} selected" },
                    { "StHashing",             "Hashing: {0}%" },
                    { "StDone",                "Finished" },
                    { "StStopped",             "Cancelled" },
                    { "StListFile",            "Checksum file: {0}" },

                    { "MsgErrorTitle",         "Error" },
                    { "MsgWarningTitle",       "Warning" },
                    { "MsgNoticeTitle",        "Notice" },
                    { "MsgDropHint",           "Drop files, folders or checksum records (.md5 / .sha256 / .sha3-256 ...) here, or click \"Add\" on the toolbar." },
                    { "MsgNoHashingTargets",   "The list is empty, there is nothing to hash." },
                    { "MsgNoPendingTargets",   "Every file in the list already has a 摘要 value." },
                    { "MsgHashRunning",        "Hashing is running. Press \"Stop\" before changing the list." },
                    { "MsgFileNotFound",       "File not found: {0}" },
                    { "MsgSaved",              "Saved {0} 摘要 value(s) to:\n{1}" },
                    { "MsgNothingToSave",      "No item has a computed 摘要 value." },
                    { "MsgSaveFailed",         "Save failed: {0}" },
                    { "MsgNoSelection",        "Please select at least one item." },
                    { "MsgClearConfirm",       "Clear the whole list ({0} item(s))?" },
                    { "MsgCopied",             "Copied to clipboard." },
                    { "MsgCopyEmpty",          "Nothing to copy." },
                    { "MsgNoHash",             "The selected item has no 摘要 value yet. Run \"Start\" first." },
                    { "MsgOverwrite",          "The file already exists:\n{0}\n\nOverwrite it?" },
                    { "MsgSaveListFilter",     "Checksum record (*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt)|*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt|Text file (*.txt)|*.txt|All files (*.*)|*.*" },
                    { "MsgFileFilter",         "Checksum record (*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt)|*.md5;*.sha1;*.sha224;*.sha256;*.sha384;*.sha512;*.sha512-224;*.sha512-256;*.sha3-224;*.sha3-256;*.sha3-384;*.sha3-512;*.blake2s;*.blake2b-256;*.blake2b;*.crc32;*.crc64;*.txt|Text file (*.txt)|*.txt|All files (*.*)|*.*" },
                    { "MsgSelfTestTitle",      "Self test: 摘要 of this program" },
                    { "MsgSelfTestBody",       "Algorithm: {0}\n\nProgram path: {1}\n\nDigest:\n{2}" },
                    { "MsgPropertiesBody",     "Path: {0}\n\nSize: {1} bytes ({2})\nModified: {3}\nAlgorithm: {4}\n\nCurrent value:\n{5}\n\nSaved value:\n{6}" },
                    { "MsgNoValue",            "(none)" },
                    { "MsgThreadsHint",        "Range 1 - {0}. Higher is usually faster, but heavy random I/O on a hard disk can slow it down." },
                    { "MsgRecordLoaded",       "Matched {0} item(s) from the checksum record: {1} (encoding: {2})." },
                    { "MsgRecordPartial",      "{0} more record(s) had no matching file on disk." },
                    { "MsgRecordEmpty",        "The checksum record contains no parsable 摘要 value." },
                    { "MsgRecordNoTarget",     "None of the {0} record(s) could be matched to a file (a missing extension is allowed)." },
                    { "MsgNotAChecksumFile",   "This file contains no parsable 摘要 value and is not a checksum record." },

                    { "OptTitle",              "Options" },
                    { "OptThreads",            "Threads" },
                    { "OptUppercase",          "Show and save 摘要 values in upper case" },
                    { "OptAutoStart",          "Start hashing automatically after adding files (off by default)" },
                    { "OptAutoLoadSidecar",    "Load checksum records from the same folder automatically (same name and extension not required)" },
                    { "OptBom",                "Write checksum files with a UTF-8 BOM" },
                    { "OptRememberWindow",     "Remember window position and size" },
                    { "OptOk",                 "OK" },
                    { "OptCancel",             "Cancel" },

                    { "CompareTitle",          "Compare Hash" },
                    { "CompareFileLabel",      "File: " },
                    { "CompareCurrentLabel",   "Current value: " },
                    { "CompareExpectedLabel",  "Saved value: " },
                    { "CompareInputLabel",     "摘要 to compare: " },
                    { "CompareResultMatch",    "Passed: the 摘要 values match." },
                    { "CompareResultMismatch", "Failed: the 摘要 values differ." },
                    { "CompareInvalid",          "This value is not a digest of any supported algorithm. Check that it was pasted completely." },
                    { "CompareResultPending",  "Enter a digest (any of MD5 / SHA-1 / SHA-2 / SHA-3 / BLAKE2 / CRC) to compare." },
                    { "CompareOk",             "OK" },
                    { "CompareCancel",         "Cancel" },
                    { "ComparePaste",          "Paste" },

                    { "CalcTitle",             "Digest Calculator" },
                    { "CalcTextLabel",         "Text (UTF-8):" },
                    { "CalcResultLabel",       "摘要: " },
                    { "CalcFromFile",          "From file..." },
                    { "CalcCompute",           "Compute" },
                    { "CalcCopy",              "Copy" },
                    { "CalcClose",             "Close" },
                    { "CalcHint",              "The value updates as you type; you can also pick a file to hash it." },

                    { "AboutTitle",            "About HashChecker" },
                    { "AboutBody",             "HashChecker\n\nVersion {0}\n\nA multi-algorithm checksum verifier.\nSupports MD5 / SHA-1 / the whole SHA-2 and SHA-3 families / BLAKE2 / CRC,\nwith checksum record I/O, two-way lookup, filtering and copying." },

                    { "ColAlgorithm", "Algorithm" },
                    { "LblAlgorithm", "Algorithm" },
                    { "TipAlgorithmFromRecord", "The algorithm comes from the checksum record extension: {0}." },
                    { "TipAlgorithmAmbiguous", "The value length matches {0} algorithms; a match on any of them passes." },
                    { "TipAlgorithmSelected", "No checksum record; hashing with the selected algorithm: {0}." },
                    { "MsgAlgorithmChanged", "Switched to {0}. Items without a checksum record will be recomputed." },
                    { "MsgRecordAlgorithm", "Record algorithm: {0}." },
                    { "MsgNoRecordHint", "No checksum record found next to this file; it is hashed but not compared." },
                    { "OptAlgorithm", "Default algorithm (used when there is no checksum record)" },
                    { "AboutOk",               "OK" },
                }
            }
        };

        /// <summary>取一条文本；键缺失时回退到英文，避免界面出现裸键名。</summary>
        public static string Get(string key)
        {
            if (Texts.TryGetValue(_currentLanguage, out var dict) &&
                dict.TryGetValue(key, out var value))
            {
                return value;
            }

            if (Texts.TryGetValue("en-US", out var fallback) &&
                fallback.TryGetValue(key, out var en))
            {
                return en;
            }

            return key;
        }

        /// <summary>取带占位符的文本并格式化。参数数量由调用方保证，编译期查不出来。</summary>
        public static string Format(string key, params object[] args)
        {
            return string.Format(Get(key), args);
        }

        private static string Get_currentLanguage()
        {
            var currentCulture = CultureInfo.CurrentUICulture;
            var supported = new HashSet<string> { "zh-CN", "zh-TW", "en-US" };
            return supported.Contains(currentCulture.Name) ? currentCulture.Name : "en-US";
        }
    }
}
