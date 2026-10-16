using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static bool AuditDesktopEnabled;
    [StructLayout(LayoutKind.Sequential)] private struct AuditRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr AuditSendMessage(IntPtr window,int message,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process,int flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out AuditRect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private static void TestAuditDpiMessage()
    {
        Exception failure=null;
        var thread=new Thread(delegate() {
            try {
                using(Form form=(Form)hub.GetMethod("BuildLauncherForm",Flags,null,new Type[] {typeof(int)},null).Invoke(null,new object[] {-1}))
                {
                    Type type=form.GetType(); type.GetField("FitWorkingArea",ReviewInstance).SetValue(form,false);
                    type.GetMethod("InitializeDpi",ReviewInstance).Invoke(form,null);
                    Size design=form.Size; float initial=(float)type.GetField("RenderScale",ReviewInstance).GetValue(form);
                    IntPtr rect=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AuditRect)));
                    try {
                        foreach(int dpi in new int[] {96,144,192,288,96})
                        {
                            int width=(int)Math.Round(design.Width/initial*dpi/96f),height=(int)Math.Round(design.Height/initial*dpi/96f);
                            width=Math.Min(width,Screen.PrimaryScreen.Bounds.Width-100); height=Math.Min(height,Screen.PrimaryScreen.Bounds.Height-100);
                            var suggested=new AuditRect {Left=-12000,Top=-12000,Right=-12000+width,Bottom=-12000+height};
                            Marshal.StructureToPtr(suggested,rect,false);
                            AuditSendMessage(form.Handle,0x02e0,new IntPtr(dpi|(dpi<<16)),rect);
                            Check(Math.Abs((float)type.GetField("RenderScale",ReviewInstance).GetValue(form)-dpi/96f)<0.001,"原生 DPI 消息驱动倍率 "+dpi);
                            Check(form.Left==-12000 && form.Top==-12000 && form.Width==width && form.Height==height,"使用 Windows 建议窗口矩形 "+dpi);
                            foreach(Control control in form.Controls)
                                Check(control.Left>=0 && control.Right<=form.ClientSize.Width+1,"WM_DPICHANGED 后控件横向可达 "+dpi+" "+control.Text);
                            Control input=null; foreach(Control control in form.Controls) if(control.GetType().Name=="ModernInput") input=control;
                            Check(input!=null && input.Font.Unit==GraphicsUnit.Pixel,"WM_DPICHANGED 使用物理像素字体");
                            if(dpi==144) {
                                string dir=Path.Combine(SourceRoot,"artifacts","audit-pending-20261010","desktop"); Directory.CreateDirectory(dir);
                                SaveDpiForm(form,Path.Combine(dir,"launcher-wm-dpi-144.png"));
                            }
                        }
                    } finally {Marshal.FreeHGlobal(rect);}
                }
            } catch(Exception ex) {failure=ex;}
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(15000),"原生 DPI 消息夹具结束"); if(failure!=null) throw failure;
    }

    private static void TestAuditDesktop()
    {
        Exception failure=null;
        var thread=new Thread(delegate() {
            string output=Path.Combine(SourceRoot,"artifacts","audit-pending-20261010","desktop"); Directory.CreateDirectory(output);
            try
            {
                var inventory=new StringBuilder(); inventory.Append("{\"monitors\":["); bool comma=false; int firstDpi=0; bool mixed=false;
                using(Form form=(Form)hub.GetMethod("BuildLauncherForm",Flags,null,new Type[] {typeof(int)},null).Invoke(null,new object[] {-1}))
                {
                    form.StartPosition=FormStartPosition.Manual;
                    foreach(Screen screen in Screen.AllScreens)
                    {
                        form.Bounds=new Rectangle(screen.WorkingArea.Left+20,screen.WorkingArea.Top+20,Math.Min(900,screen.WorkingArea.Width-40),Math.Min(700,screen.WorkingArea.Height-40));
                        form.Show(); Application.DoEvents(); Thread.Sleep(100); Application.DoEvents();
                        int dpi=(int)GetDpiForWindow(form.Handle); float scale=(float)form.GetType().GetField("RenderScale",ReviewInstance).GetValue(form);
                        Check(Math.Abs(scale-dpi/96f)<0.001,"真实显示器上的窗口倍率等于 GetDpiForWindow："+dpi);
                        if(firstDpi==0) firstDpi=dpi; else if(dpi!=firstDpi) mixed=true;
                        if(comma) inventory.Append(','); comma=true;
                        inventory.Append("{\"dpi\":").Append(dpi).Append(",\"width\":").Append(screen.Bounds.Width).Append(",\"height\":").Append(screen.Bounds.Height).Append('}');
                    }
                    form.Hide();
                }
                inventory.Append("],\"physicalMixedDpi\":\"").Append(mixed?"verified":"unverified: no monitors with different DPI").Append("\"}");
                File.WriteAllText(Path.Combine(output,"monitor-inventory.json"),inventory.ToString(),Encoding.UTF8);

                // 只截取本夹具自己的白底窗口和覆盖层，验证实际桌面合成后的像素。
                using(var background=new Form())
                {
                    background.FormBorderStyle=FormBorderStyle.None; background.BackColor=Color.White; background.ClientSize=new Size(320,180);
                    background.AutoScaleMode=AutoScaleMode.None;
                    background.StartPosition=FormStartPosition.Manual; background.Location=new Point(Screen.PrimaryScreen.WorkingArea.Left+50,Screen.PrimaryScreen.WorkingArea.Top+50);
                    background.TopMost=true; background.Show(); background.BringToFront(); SetForegroundWindow(background.Handle); background.Refresh(); Application.DoEvents(); DwmFlush();
                    AuditRect br; GetWindowRect(background.Handle,out br);
                    Console.WriteLine("background managed="+background.Bounds+" native="+br.Left+","+br.Top+","+br.Right+","+br.Bottom);
                    Type configType=DanmakuType("DanmakuConfig"); object config=Activator.CreateInstance(configType);
                    configType.GetField("SolidText").SetValue(config,true);
                    using(Form overlay=(Form)Activator.CreateInstance(DanmakuType("DanmakuOverlayForm"),new object[] {config,Path.Combine(output,"synthetic-config.json"),0,"DPI audit fixture"}))
                    {
                        overlay.Owner=background;
                        Type type=overlay.GetType(); type.GetMethod("EnsureCanvas",ReviewInstance).Invoke(overlay,new object[] {320,180});
                        Bitmap canvas=(Bitmap)type.GetField("_canvas",ReviewInstance).GetValue(overlay);
                        using(Graphics g=Graphics.FromImage(canvas))
                        {
                            g.Clear(Color.Transparent);
                            using(var brush=new SolidBrush(Color.FromArgb(128,255,0,0))) g.FillRectangle(brush,10,10,80,80);
                            g.FillRectangle(Brushes.Black,110,10,80,80);
                        }
                        Type rt=type.GetNestedType("RECT",BindingFlags.NonPublic); object r=Activator.CreateInstance(rt);
                        rt.GetField("Left").SetValue(r,br.Left); rt.GetField("Top").SetValue(r,br.Top);
                        rt.GetField("Right").SetValue(r,br.Left+320); rt.GetField("Bottom").SetValue(r,br.Top+180);
                        MethodInfo blit=type.GetMethod("Blit",ReviewInstance); IntPtr hwnd=overlay.Handle;
                        int gdiBefore=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                        for(int i=0;i<100;i++) blit.Invoke(overlay,new object[] {r});
                        Application.DoEvents(); DwmFlush(); Thread.Sleep(200);
                        using(var screenshot=new Bitmap(320,180))
                        {
                            using(Graphics g=Graphics.FromImage(screenshot)) g.CopyFromScreen(new Point(br.Left,br.Top),Point.Empty,screenshot.Size);
                            Color clear=screenshot.GetPixel(250,100),blend=screenshot.GetPixel(50,50),solid=screenshot.GetPixel(150,50);
                            Console.WriteLine("desktop pixels clear="+clear+" blend="+blend+" solid="+solid);
                            Check(clear.R>245 && clear.G>245 && clear.B>245,"透明区域显示真实白底桌面像素");
                            screenshot.Save(Path.Combine(output,"layered-desktop-pixels.png"),ImageFormat.Png);
                            Check(blend.R>245 && Math.Abs(blend.G-127)<8 && Math.Abs(blend.B-127)<8,"分层窗口的半透明红色按 alpha 合成");
                            Check(solid.R<8 && solid.G<8 && solid.B<8,"分层窗口实心像素保持不透明");
                        }
                        Check(GetGuiResources(Process.GetCurrentProcess().Handle,0)<=gdiBefore+5,"100 次实际分层贴图无持续 GDI 句柄增长");
                    }
                }
            }
            catch(Exception ex) {failure=ex;}
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(30000),"桌面实验结束"); if(failure!=null) throw failure;
    }
}
