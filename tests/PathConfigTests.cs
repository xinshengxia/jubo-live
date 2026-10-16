// --------------------------------------------------------------------------------------------
// 路径解析（评估报告 §8：开发路径仍写死在源码）。
//
// 判据是**端到端**的：真的用命令行/配置文件改一次路径，让启动器把 --self-test 报告写出来，
// 再断言报告里那几行 TOOL xxx = ... <- 来源。只测「函数返回了字符串」是不够的 ——
// 这条链断在哪一环都会让用户「改了没效果」，而那种问题从表面上完全看不出来。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

internal static partial class HardeningTests
{
    private static void TestPathResolution(string launcher)
    {
        // ---- ① --xxx= 参数必须被摘出来，且**不能**污染后面的位置参数 ----
        object rest = Call("ApplyPathOverrides", new object[] {
            new string[] { "--mpv=C:\\tmp\\mpv.exe", "--self-test" } });
        string[] kept = rest as string[];
        Check(kept != null && kept.Length == 1 && kept[0] == "--self-test",
            "--self-test 前面的 --mpv= 被摘掉，位置参数不受影响（" + (kept == null ? "null" : kept.Length + " 个") + "）");

        // ---- ② 变量展开与相对路径 ----
        string expanded = (string)Call("ExpandPath", new object[] { "%TEMP%\\livehub-x", null });
        string temp = Path.GetTempPath().TrimEnd('\\');
        Check(expanded != null && expanded.StartsWith(temp, StringComparison.OrdinalIgnoreCase),
            "%VAR% 会被展开（实得 " + expanded + "）");
        string relative = (string)Call("ExpandPath", new object[] { "sub\\tool.exe", @"C:\base" });
        Check(relative == @"C:\base\sub\tool.exe", "相对路径按基准目录解析（实得 " + relative + "）");

        // ---- ③ PATH 查找 ----
        object found = Call("FindOnPath", new object[] { new string[] { "cmd.exe" } });
        Check(found is string && File.Exists((string)found), "PATH 里能找到 cmd.exe（实得 " + found + "）");
        object missing = Call("FindOnPath", new object[] { new string[] { "livehub-no-such-tool-xyz.exe" } });
        Check(missing == null, "PATH 里找不到就返回 null，不瞎猜");

        // ---- ④ 端到端：命令行覆盖 + 配置文件，都要反映到自检报告里 ----
        string dir = Path.Combine(Path.GetTempPath(), "livehub-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string fakeConfig = Path.Combine(dir, "live-hub.json");
            File.WriteAllText(fakeConfig,
                "{\"ytdlp\": \"" + Path.Combine(dir, "yt-dlp-fake.exe").Replace("\\", "\\\\") + "\", \"runtimeDir\": \""
                + dir.Replace("\\", "\\\\") + "\"}", Encoding.UTF8);
            // 必须**真的存在**：ResolveMpvPlan 会 File.Exists 检查并抛「找不到 mpv」。
            // （这条本身就说明路径解析的结果是被真正用起来的，不是摆设。）
            string fakeMpv = Path.Combine(dir, "mpv.exe");
            File.WriteAllBytes(fakeMpv, new byte[0]);
            File.WriteAllBytes(Path.Combine(dir, "yt-dlp-fake.exe"), new byte[0]);

            // 便携布局（exe 旁边有 bin\mpv\mpv.exe）下：命令行覆盖仍然生效，但**配置文件不参与** ——
            // 那是设计意图（包拷到哪都一样跑），所以两种布局断言不同。
            bool portable = File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(launcher)),
                "bin", "mpv", "mpv.exe"));

            // --runtime 走命令行：两种布局都必须生效（便携包也该听显式命令行覆盖）；
            // 于是报告一定落在 $dir 里，下面就能按布局分别断言别的路径。
            int code = RunLauncherExitCode(launcher,
                "--self-test --mpv=\"" + fakeMpv + "\" --config=\"" + fakeConfig + "\" --runtime=\"" + dir + "\"");
            string reportPath = Path.Combine(dir, "selftest.txt");
            Check(code == 0, "--self-test 带路径覆盖参数仍能通过（退出码 " + code + "）");
            Check(File.Exists(reportPath), "--self-test 报告写到 --runtime 指定的目录（" + reportPath + "）");
            if (File.Exists(reportPath))
            {
                string report = File.ReadAllText(reportPath, Encoding.UTF8);
                Check(report.IndexOf("TOOL mpv = " + fakeMpv, StringComparison.OrdinalIgnoreCase) >= 0,
                    "自检报告里的 mpv 就是命令行给的那个（命令行优先级最高，便携布局也听）");
                Check(report.IndexOf("<- 命令行 --mpv=", StringComparison.Ordinal) >= 0,
                    "自检报告写明 mpv 来自命令行覆盖");
                Check(report.IndexOf("CONFIG=" + fakeConfig, StringComparison.OrdinalIgnoreCase) >= 0,
                    "自检报告写明用的是哪个配置文件");
                Check(report.IndexOf("TOOL runtime = " + dir, StringComparison.OrdinalIgnoreCase) >= 0,
                    "runtime 目录能被 --runtime 覆盖（两种布局都成立）");

                if (portable)
                {
                    // 便携布局：配置文件**刻意不参与**（包拷到哪都一样跑），所以 yt-dlp 应当还是包内那份。
                    string bundled = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(launcher)), "bin", "yt-dlp.exe");
                    Check(report.IndexOf("TOOL yt-dlp = " + bundled, StringComparison.OrdinalIgnoreCase) >= 0,
                        "便携布局：yt-dlp 用包内那份（配置文件不参与，这是设计意图）");
                    Check(report.IndexOf(Path.Combine(dir, "yt-dlp-fake.exe"), StringComparison.OrdinalIgnoreCase) < 0,
                        "便携布局：配置文件里的路径确实没被采用");
                }
                else
                {
                    Check(report.IndexOf("TOOL yt-dlp = " + Path.Combine(dir, "yt-dlp-fake.exe"), StringComparison.OrdinalIgnoreCase) >= 0,
                        "开发布局：配置文件里的 yt-dlp 生效了");
                    Check(report.IndexOf("<- 配置文件 live-hub.json", StringComparison.Ordinal) >= 0,
                        "自检报告写明 yt-dlp 来自配置文件");
                }
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}