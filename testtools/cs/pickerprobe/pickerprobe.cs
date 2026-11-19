using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

/// <summary>
/// 界面侧：BLAKE3 必须真的出现在下拉框里、能被选中、并驱动计算。
/// 内核与目录都对了，但下拉框没列出来，用户一样用不上。
/// </summary>
class PickerProbe
{
    static int _failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    [STAThread]
    static int Main(string[] args)
    {
        RedirectSettingsToTemp();
        if (args.Length < 1) { Console.WriteLine("用法: pickerprobe.exe <HashChecker.exe>"); return 2; }

        string asmPath = Path.GetFullPath(args[0]);
        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Type windowType = asm.GetType("HashChecker.MainWindow");
        if (windowType == null) { Console.WriteLine("!! 找不到 MainWindow"); return 1; }

        Console.WriteLine("=== 算法下拉框里必须有 BLAKE3 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        Window window = (Window)Activator.CreateInstance(windowType);
        try
        {
            ComboBox picker = (ComboBox)windowType
                .GetField("AlgorithmPicker", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .GetValue(window);

            var names = new System.Collections.Generic.List<string>();
            int blake3Index = -1;
            for (int i = 0; i < picker.Items.Count; i++)
            {
                string display = picker.Items[i].ToString();
                names.Add(display);
                if (display == "BLAKE3") blake3Index = i;
            }

            Console.WriteLine("  下拉框共 " + picker.Items.Count + " 项：");
            Console.WriteLine("  " + string.Join(" / ", names.ToArray()));
            Console.WriteLine("");

            Check("下拉框里出现 BLAKE3", blake3Index >= 0, blake3Index >= 0 ? "第 " + (blake3Index + 1) + " 项" : "没找到");
            Check("下拉框项数与算法目录一致", picker.Items.Count == CountAlgorithms(asm), "下拉框 " + picker.Items.Count + " 项");
            Check("显示名没有重复", NoDuplicates(names), "");

            if (blake3Index >= 0)
            {
                picker.SelectedIndex = blake3Index;

                object selected = picker.SelectedItem;
                string id = (string)selected.GetType().GetProperty("Id").GetValue(selected);
                Check("选中后 Id 是 blake3", id == "blake3", id);

                Type hashValues = asm.GetType("HashChecker.Models.HashValues");
                MethodInfo hashText = hashValues.GetMethod("HashText", BindingFlags.Public | BindingFlags.Static);
                string abc = (string)hashText.Invoke(null, new object[] { "abc", selected });

                Check("下拉框选中的算法能算出正确的值",
                    abc == "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85", abc);
            }
        }
        finally
        {
            window.Close();
        }

        Console.WriteLine("");
        if (_failures == 0) { Console.WriteLine("全部符合预期 ✓"); return 0; }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static int CountAlgorithms(Assembly asm)
    {
        Type algorithms = asm.GetType("HashChecker.Services.HashAlgorithms");
        object list = algorithms.GetProperty("All", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        int count = 0;
        foreach (object unused in (System.Collections.IEnumerable)list) count++;
        return count;
    }

    static bool NoDuplicates(System.Collections.Generic.List<string> names)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names) if (!seen.Add(name)) return false;
        return true;
    }

    /// <summary>
    /// 把配置目录改道到临时目录。必须在触碰产品任何类型之前调用 ——
    /// 探针用 LoadFrom 载入产品程序集后，产品会拿「入口程序集名」当配置目录名，
    /// 那个名字就是本探针，配置会落到 %LOCALAPPDATA%\<探针名>\ 里，
    /// 每跑一次就污染一次真实用户配置。
    /// </summary>
    static void RedirectSettingsToTemp()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "HashChecker", "settings-" + Path.GetFileNameWithoutExtension(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("HASHCHECKER_SETTINGS_DIR", dir);
        }
        catch
        {
            // 改道失败不该让探针跑不起来：最坏退回原位，测试结论不受影响。
        }
    }
}