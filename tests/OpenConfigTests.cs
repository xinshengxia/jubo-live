// --------------------------------------------------------------------------------------------
// 「打开配置文件」不许把用户丢进「你要如何打开这个文件？」选择框。
//
// 产品里打开**文档**的入口包括覆盖层右键菜单的
// 「打开设置文件…」，老实现是 Process.Start("notepad.exe", path)：
//   · 走 ShellExecute → 靠**系统文件关联**找记事本；
//   · .json / .txt 可能关联第三方编辑器，商店版记事本也可能经过重定向；
//   · 关联链断一节，Windows 就弹选择框 —— 而那个框是死路（选完也改不到配置，还挡住画面）。
// 现在改成绝对路径的记事本（UseShellExecute=false，不查关联），没有就资源管理器选中文件。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static partial class HardeningTests
{
    private static void TestOpenConfigWithoutPicker(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type hub = assembly.GetType("LiveHub", true);
        BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

        // ---- ① 记事本解析：有的话必须是**绝对路径**且真的存在 ----
        MethodInfo editorPath = hub.GetMethod("EditorPath", flags);
        Check(editorPath != null, "打开配置：LiveHub.EditorPath 存在（否则这条是空转）");
        if (editorPath == null) return;
        string editor = (string)editorPath.Invoke(null, null);
        if (editor != null)
        {
            Check(Path.IsPathRooted(editor), "打开配置：记事本路径是绝对路径（实得 " + editor + "）");
            Check(File.Exists(editor), "打开配置：记事本文件真的存在（" + editor + "）");
            Check(editor.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0,
                "打开配置：取的是系统目录里的记事本，不是 PATH 上碰运气的同名程序");
        }
        else
        {
            Check(true, "打开配置：本机没有记事本 → 走资源管理器退路（不算失败）");
        }

        // ---- ② 源码级护栏：产品代码里不许再出现「裸 notepad 启动」这种走关联的写法 ----
        // 和「空 catch 棘轮」一个思路：把踩过的坑钉成规则，防止下次又写回去。
        int offenders = 0;
        string where = "";
        // ⚠️ 只扫**产品源码**（根目录 live-hub-v2.cs + src\**）：不能对仓库根做递归扫描 ——
        //    runtime\ 下有程序自己加了限制 ACL 的浏览器 profile 目录（douyin-login-profile 等），
        //    递归进去直接 UnauthorizedAccessException（2026-09-27 实测踩到）。
        List<string> sources = new List<string>();
        try { sources.AddRange(Directory.GetFiles(SourceRoot, "*.cs", SearchOption.TopDirectoryOnly)); }
        catch (Exception) { }
        string srcDir = Path.Combine(SourceRoot, "src");
        if (Directory.Exists(srcDir))
        {
            try { sources.AddRange(Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)); }
            catch (Exception) { }
        }
        Check(sources.Count > 5, "打开配置：扫到了产品源码（实得 " + sources.Count + " 个 .cs）");
        foreach (string file in sources)
        {
            foreach (string line in File.ReadAllLines(file))
            {
                string trimmed = line.TrimStart();
                // 注释里写「以前是 Process.Start("notepad.exe", …)」是**说明**，不是代码 —— 跳过。
                // （2026-09-27 实测踩到：第一版把解释这条规则的注释自己判成了违规。）
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                // 合规写法是 ProcessStartInfo(editor, ...) + Process.Start(start)：
                // 同一行里既出现 Process.Start 又出现 notepad，就是要抓的裸启动。
                if (line.IndexOf("Process.Start", StringComparison.OrdinalIgnoreCase) >= 0
                    && line.IndexOf("notepad", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    offenders++;
                    where = Path.GetFileName(file) + "：" + line.Trim();
                }
            }
        }
        Check(offenders == 0, "打开配置：产品代码里没有裸 notepad 启动（走关联就可能弹选择框）"
            + (offenders > 0 ? "：" + where : ""));
    }
}