using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private static int RunAuditImageMemory(string launcher,string imagePath,string report)
    {
        try
        {
            Assembly assembly=Assembly.LoadFrom(launcher); MethodInfo decode=assembly.GetType("DanmakuImageCache",true).GetMethod("DecodeImage",ReviewStatic);
            byte[] bytes=File.ReadAllBytes(imagePath);
            using(Bitmap warmup=(Bitmap)decode.Invoke(null,new object[] {MakePng(32,32)})) { }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Process process=Process.GetCurrentProcess(); process.Refresh(); long initial=process.PrivateMemorySize64,peak=initial;
            int gdi=GetGuiResources(process.Handle,0),peakGdi=gdi,rejected=0;
            for(int i=0;i<8;i++)
            {
                using(Bitmap result=(Bitmap)decode.Invoke(null,new object[] {bytes}))
                { if(result==null) rejected++; process.Refresh(); peak=Math.Max(peak,process.PrivateMemorySize64); peakGdi=Math.Max(peakGdi,GetGuiResources(process.Handle,0)); Thread.Sleep(10); }
            }
            process.Refresh();
            File.WriteAllText(report,"{\"iterations\":8,\"rejected\":"+rejected+",\"initialPrivateBytes\":"+initial+",\"peakPrivateBytes\":"+peak+",\"growthBytes\":"+(peak-initial)+",\"gdiGrowth\":"+(peakGdi-gdi)+"}");
            return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
