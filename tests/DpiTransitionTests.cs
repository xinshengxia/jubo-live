using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    [StructLayout(LayoutKind.Sequential)] private struct TransitionSize { public int Width, Height; }
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectExForDpi(ref AuditRect rect, int style, bool menu, int exStyle, uint dpi);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int TransitionWindowStyle(IntPtr hwnd, int index);

    private static Form TransitionForm()
    {
        Form form = (Form)Activator.CreateInstance(DanmakuType("DpiForm"), ReviewInstance, null, new object[0], null);
        form.ClientSize = new Size(701, 503); form.MinimumSize = new Size(100, 80);
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-12000, -12000);
        form.GetType().GetField("FitWorkingArea", ReviewInstance).SetValue(form, false);
        form.GetType().GetMethod("InitializeDpi", ReviewInstance).Invoke(form, null);
        return form;
    }

    private static void WithTransitionThread(Action test)
    {
        Exception failure = null;
        var thread = new Thread(delegate() { try { test(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(15000), "隐藏 DPI 消息夹具结束"); if (failure != null) throw failure;
    }

    private static void SendTransition(Form form, int dpi, Size size)
    {
        IntPtr rect = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AuditRect)));
        try
        {
            Marshal.StructureToPtr(new AuditRect { Left=-12000, Top=-12000, Right=-12000+size.Width, Bottom=-12000+size.Height }, rect, false);
            AuditSendMessage(form.Handle, 0x02e0, new IntPtr(dpi | (dpi << 16)), rect);
        }
        finally { Marshal.FreeHGlobal(rect); }
    }

    private static void TestFrameworkDpiTransition()
    {
        // Synthetic messages isolate the production branch; no desktop scale changes.
        WithTransitionThread(delegate
        {
            using (Form launcher = (Form)hub.GetMethod("BuildLauncherForm", Flags, null, new Type[] {typeof(int)}, null).Invoke(null, new object[] {-1}))
            {
                launcher.GetType().GetField("FitWorkingArea", ReviewInstance).SetValue(launcher, false);
                launcher.GetType().GetMethod("InitializeDpi", ReviewInstance).Invoke(launcher, null);
                foreach (int dpi in new[] {96,120,144,192})
                {
                    SendTransition(launcher, dpi, new Size(2300,1500));
                    Check(launcher.DeviceDpi == dpi, "框架 DeviceDpi 同步原生消息 " + dpi + " actual=" + launcher.DeviceDpi);
                    Check(Math.Abs((float)launcher.GetType().GetField("RenderScale",ReviewInstance).GetValue(launcher)-dpi/96f)<0.001, "应用倍率同步 " + dpi);
                    foreach(Control c in launcher.Controls) if(c.Text=="加入收藏")
                        Check(c.Font.Unit==GraphicsUnit.Pixel && Math.Abs(c.Font.Size-9f*dpi/72f)<0.01f, "框架衔接不会重复缩放字体 " + dpi);
                }
            }
        });
    }

    private static Size NegotiateTransition(Form form, int dpi, SizeF logical)
    {
        IntPtr pointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TransitionSize)));
        try
        {
            Marshal.StructureToPtr(new TransitionSize {Width=form.Width,Height=form.Height},pointer,false);
            IntPtr handled=AuditSendMessage(form.Handle,0x02e4,new IntPtr(dpi),pointer);
            Check(handled!=IntPtr.Zero,"提供按客户区计算的DPI建议外框 " + dpi);
            TransitionSize result=(TransitionSize)Marshal.PtrToStructure(pointer,typeof(TransitionSize));
            var expected=new AuditRect {Right=(int)Math.Round(logical.Width*dpi/96d),Bottom=(int)Math.Round(logical.Height*dpi/96d)};
            Check(AdjustWindowRectExForDpi(ref expected,TransitionWindowStyle(form.Handle,-16),false,TransitionWindowStyle(form.Handle,-20),(uint)dpi),"原生目标DPI边框计算成功");
            Check(result.Width==expected.Right-expected.Left && result.Height==expected.Bottom-expected.Top,"DPI外框只包含目标倍率边框 " + dpi);
            return new Size(result.Width,result.Height);
        }
        finally {Marshal.FreeHGlobal(pointer);}
    }

    private static void TestDpiClientSizeRoundTrip()
    {
        WithTransitionThread(delegate
        {
            using(Form form=TransitionForm())
            {
                int current=(int)GetDpiForWindow(form.Handle);
                foreach(bool resized in new[]{false,true})
                {
                    if(resized) form.ClientSize=new Size(1353,997);
                    Size original=form.ClientSize;
                    SizeF logical=new SizeF(original.Width*96f/current,original.Height*96f/current);
                    for(int round=0;round<3;round++)
                    {
                        foreach(int dpi in new[]{96,120,144,168,192,current})
                            SendTransition(form,dpi,NegotiateTransition(form,dpi,logical));
                        Application.DoEvents();
                        Check(form.ClientSize==original,"倍率往返客户区无累计误差 resized="+resized+" round="+round);
                        Rectangle bounds=form.Bounds;
                        for(int i=0;i<3;i++) { form.GetType().GetMethod("QueueDpiReconcile",ReviewInstance).Invoke(form,null); Application.DoEvents(); }
                        Check(form.Bounds==bounds,"同倍率延迟复核不改窗口外框");
                    }
                }
            }
        });
    }
}
