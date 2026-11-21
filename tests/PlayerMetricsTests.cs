using System;
using System.IO;

internal static partial class HardeningTests
{
    private static void TestPlayerMetrics()
    {
        string directory = ReviewDirectory();
        string state = Path.Combine(directory, "state");
        try
        {
            File.WriteAllText(state + ".metrics0", "v1 100 42 2.630 0 31 1.800\n");
            File.WriteAllText(state + ".metrics1", "v1 101 43 9 9 9 9\n");
            string actual = (string)Call("ReadMpvMetrics", state, 42, 101L);
            Check(actual.Contains("+2.63 ms") && actual.Contains("解码 0 / 输出 31") && actual.Contains("1.80 s"), "计数、单位与 PID 归属");
            File.WriteAllText(state + ".metrics1", "v1 101 42 - - 0 -\n");
            string partial = (string)Call("ReadMpvMetrics", state, 42, 101L);
            Check(partial.Contains("A-V —") && partial.Contains("解码 — / 输出 0"), "缺失与真实零分开");
            File.WriteAllText(state + ".metrics1", "v1 101 42 20 99");
            Check(actual == (string)Call("ReadMpvMetrics", state, 42, 101L), "另一槽写入中仍可取完整样本");
            Check(((string)Call("ReadMpvMetrics", state, 42, 105L)).Contains("暂无"), "过期样本不冒充实时值");
        }
        finally { Directory.Delete(directory, true); }
    }
}
