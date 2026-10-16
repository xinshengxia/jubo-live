// --------------------------------------------------------------------------------------------
// 离线回归的**测试运行器**（2026-09-27 新增，评估报告 §2 的整改项）。
//
// 为什么需要它：原来 `Check(false, ...)` 直接抛异常，`Main` 里那一串顺序调用
// **第一个失败就把后面全部跳过了** —— 报告实测踩到：WebP 那条一红，排在它后面的
// 虎牙收割表 / Tars 容错等新测试**根本没跑**，而脚本只打印构建输出最后六行，
// 连失败项的名字都看不见。于是「测试是红的」这句话在当时几乎不携带信息。
//
// 现在的语义：
//   · 每个测试用例（TestCase）独立 try/catch —— 一个用例失败不影响后面的用例；
//   · 用例内部第一条失败的 Check 立即中止**该用例**（其余检查没有意义）；
//   · 系统能力不足（缺 WebP 解码器 / 缺字体 / 目录不支持 ACL）→ Skip，**不算失败**，
//     单列一类，避免「一台机器少装一个可选组件」把整条流水线判死；
//   · 结束打印 passed / failed / skipped / checks / 耗时，并可选写 JSON 报告；
//   · 退出码 0 = 无失败（skip 不影响）。
//
// 依赖：只用 .NET Framework 自带类型（正式发行用的 legacy csc 是 C# 5 编译器，
// 所以这里不许用字符串插值 / ?. / nameof / 表达式体成员）。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>用例主动跳过（系统不具备该能力），不算失败。</summary>
internal sealed class TestSkippedException : Exception
{
    public TestSkippedException(string reason) : base(reason) { }
}

/// <summary>用例内的断言失败。消息里带的是失败的那条检查名。</summary>
internal sealed class TestFailedException : Exception
{
    public TestFailedException(string message) : base(message) { }
}

internal static class TestRunner
{
    internal sealed class TestCase
    {
        public readonly string Category;
        public readonly string Name;
        public readonly Action Body;
        public TestCase(string category, string name, Action body)
        {
            Category = category; Name = name; Body = body;
        }
    }

    internal sealed class TestResult
    {
        public string Category;
        public string Name;
        public string Status;      // passed / failed / skipped
        public string Message;
        public long DurationMs;
        public int Checks;
    }

    private static readonly List<TestCase> Cases = new List<TestCase>();
    private static readonly List<TestResult> Results = new List<TestResult>();
    private static int _currentChecks;

    public static void Add(string category, string name, Action body)
    {
        Cases.Add(new TestCase(category, name, body));
    }

    // Native window geometry needs an interactive desktop; CI service sessions do not provide one.
    public static bool HasInteractiveDesktop
    {
        get { return Environment.UserInteractive && Process.GetCurrentProcess().SessionId != 0; }
    }
    public static bool Headless;

    public static void AddDesktop(string category, string name, Action body)
    {
        Add(category, name, delegate
        {
            if (Headless) Skip("Headless verification explicitly selected; native layout/DPI not run");
            if (!HasInteractiveDesktop) Skip("Interactive desktop unavailable; native layout/DPI unverified here");
            body();
        });
    }

    public static int CaseCount { get { return Cases.Count; } }

    /// <summary>用例主动跳过：系统不具备该能力。不计失败，单独统计。</summary>
    public static void Skip(string reason)
    {
        throw new TestSkippedException(reason);
    }

    /// <summary>Assert 成功：记一条 PASS（保留原来的 `PASS: ` 行格式，脚本在数它）。</summary>
    public static void Pass(string checkName)
    {
        _currentChecks++;
        Console.WriteLine("PASS: " + checkName);
    }

    /// <summary>Assert 失败：立刻打印 `FAIL: ` 那一行（脚本用它定位第一条失败），再中止本用例。</summary>
    public static void Fail(string checkName)
    {
        Console.WriteLine("FAIL: " + checkName);
        throw new TestFailedException(checkName);
    }

    public static void List()
    {
        foreach (TestCase c in Cases) Console.WriteLine(c.Category + "\t" + c.Name);
    }

    /// <summary>跑全部（或按 filter 子串过滤）用例。返回失败数。</summary>
    public static int Run(string filter, string reportPath, bool verbose)
    {
        DateTime startedAt = DateTime.Now;
        Stopwatch total = Stopwatch.StartNew();
        int passed = 0, failed = 0, skipped = 0, checks = 0;
        foreach (TestCase testCase in Cases)
        {
            if (!string.IsNullOrEmpty(filter)
                && testCase.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                && testCase.Category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            TestResult result = new TestResult();
            result.Category = testCase.Category;
            result.Name = testCase.Name;
            result.Status = "passed";
            result.Message = "";
            _currentChecks = 0;

            if (verbose) Console.WriteLine("---- [" + testCase.Category + "] " + testCase.Name);
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                testCase.Body();
            }
            catch (TestSkippedException skip)
            {
                result.Status = "skipped";
                result.Message = skip.Message;
            }
            catch (TestFailedException fail)
            {
                result.Status = "failed";
                result.Message = fail.Message;
            }
            catch (Exception ex)
            {
                // 反射调用会把真实异常包在 TargetInvocationException 里 —— 报告里要看到内层。
                Exception inner = ex;
                while (inner is System.Reflection.TargetInvocationException && inner.InnerException != null)
                    inner = inner.InnerException;
                result.Status = "failed";
                result.Message = inner.GetType().Name + ": " + inner.Message;
                Console.WriteLine("FAIL: " + testCase.Name + " 异常 " + result.Message);
                if (verbose) Console.WriteLine(inner.ToString());
            }
            watch.Stop();
            result.DurationMs = watch.ElapsedMilliseconds;
            result.Checks = _currentChecks;
            Results.Add(result);

            if (result.Status == "passed") { passed++; checks += result.Checks; }
            else if (result.Status == "skipped")
            {
                skipped++;
                Console.WriteLine("SKIP: " + testCase.Name + "  — " + result.Message);
            }
            else
            {
                failed++;
                Console.WriteLine("FAILED-CASE: [" + testCase.Category + "] " + testCase.Name
                    + "  — " + result.Message + "  （" + result.DurationMs + " ms）");
            }
        }
        total.Stop();
        DateTime finishedAt = DateTime.Now;

        Console.WriteLine("");
        Console.WriteLine("===== 离线测试汇总 =====");
        Console.WriteLine("  用例：通过 " + passed + " / 失败 " + failed + " / 跳过 " + skipped
            + "   （共 " + Cases.Count + " 个）");
        Console.WriteLine("  断言：PASS " + checks + " 条");
        Console.WriteLine("  耗时：" + total.ElapsedMilliseconds + " ms");
        if (skipped > 0)
        {
            Console.WriteLine("  跳过的用例（系统能力不足，非失败）：");
            foreach (TestResult r in Results)
                if (r.Status == "skipped") Console.WriteLine("    · " + r.Name + "  — " + r.Message);
        }
        if (failed > 0)
        {
            Console.WriteLine("  失败的用例：");
            foreach (TestResult r in Results)
                if (r.Status == "failed") Console.WriteLine("    · [" + r.Category + "] " + r.Name + "  — " + r.Message);
        }
        Console.WriteLine(failed == 0 ? "全部通过" : ("有 " + failed + " 个用例失败"));

        if (!string.IsNullOrEmpty(reportPath))
        {
            try
            {
                File.WriteAllText(reportPath, BuildJson(startedAt, finishedAt, total.ElapsedMilliseconds,
                    passed, failed, skipped, checks), new UTF8Encoding(false));
                Console.WriteLine("  报告：" + Path.GetFullPath(reportPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  报告写入失败（不影响退出码）：" + ex.Message);
            }
        }
        return failed;
    }

    private static string BuildJson(DateTime startedAt, DateTime finishedAt, long durationMs,
        int passed, int failed, int skipped, int checks)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"schema\": 1,\n");
        sb.Append("  \"startedAt\": \"" + startedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\",\n");
        sb.Append("  \"finishedAt\": \"" + finishedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\",\n");
        sb.Append("  \"durationMs\": " + durationMs + ",\n");
        sb.Append("  \"totals\": { \"passed\": " + passed + ", \"failed\": " + failed
            + ", \"skipped\": " + skipped + ", \"checks\": " + checks + " },\n");
        sb.Append("  \"tests\": [\n");
        for (int i = 0; i < Results.Count; i++)
        {
            TestResult r = Results[i];
            sb.Append("    { \"category\": " + Quote(r.Category)
                + ", \"name\": " + Quote(r.Name)
                + ", \"status\": " + Quote(r.Status)
                + ", \"durationMs\": " + r.DurationMs
                + ", \"checks\": " + r.Checks
                + ", \"message\": " + Quote(r.Message) + " }");
            sb.Append(i + 1 < Results.Count ? ",\n" : "\n");
        }
        sb.Append("  ]\n}");
        return sb.ToString();
    }

    private static string Quote(string text)
    {
        if (text == null) return "null";
        StringBuilder sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
