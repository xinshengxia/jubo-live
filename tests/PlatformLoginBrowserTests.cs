using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterLoginBrowserCases()
    {
        TestRunner.Add("扫码登录", "斗鱼明确授权跳转、完整SSO与作用域保存", TestDouyuLoginRedirect);
        TestRunner.Add("扫码登录", "慢检测时界面可点击、关闭后不保存、旧账号响应隔离", TestLoginBrowserInteraction);
        TestRunner.Add("扫码登录", "单窗口布局、多倍率、禁止跨平台跳转", TestLoginBrowserLayout);
    }
    private static void TestDouyuLoginRedirect()
    {
        Type health = DanmakuType("LoginHealth");
        string redirect = "https://www.douyu.com/api/passport/login?callback=livehubLoginCheck&client_id=&code=synthetic-code&uid=123";
        Check((bool)ReviewCall(health, "DouyuAuthRedirect", redirect), "官方实测空client_id的明确授权成功跳转可识别");
        foreach (string invalid in new[] {
            redirect.Replace("www.douyu.com", "www.douyu.com.evil.example"), redirect.Replace("https:", "http:"),
            redirect.Replace("/api/passport/login", "/member/login"), redirect.Replace("code=synthetic-code", "code="),
            redirect.Replace("uid=123", "uid=0"), redirect.Replace("client_id=", "client_id=999"),
            redirect.Replace("livehubLoginCheck", "otherCallback"), redirect + "&uid=456" })
            Check(!(bool)ReviewCall(health, "DouyuAuthRedirect", invalid), "其他跳转/缺少授权证据/重复字段不能当作成功");
        string dir = ReviewDirectory(); FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic);
        object oldRuntime = runtime.GetValue(null); FieldInfo transport = health.GetField("Transport", ReviewStatic); object old = transport.GetValue(null);
        try
        {
            runtime.SetValue(null, dir);
            object records = NewRecordList(NewRecord("acf_auth", "synthetic-business", "www.douyu.com", "/", true, true, 0),
                NewRecord("LTP0", "synthetic-sso", "passport.douyu.com", "/", true, true, 0));
            transport.SetValue(null, LookupMakeDelegate(transport.FieldType, delegate {
                object reply = HealthReply(health, 302, ""); LookupSet(reply, "Location", redirect); return reply; }));
            object result = ReviewCall(health, "CheckRecords", "douyu", records);
            Check(LoginState(result) == "valid", "完整流程明确识别SSO成功，不把302一律判为受限");
            object platform = ReviewCall(DanmakuType("PlatformLogin"), "Get", "douyu");
            Check((bool)ReviewCall(DanmakuType("LoginCapture"), "Save", platform, records, result), "统一保存流程成功写入完整会话");
            string passport = (string)ReviewCall(DanmakuType("PlatformLogin"), "CookieHeaderForUri", new Uri("https://passport.douyu.com/"));
            string business = (string)ReviewCall(DanmakuType("PlatformLogin"), "CookieHeaderForUri", new Uri("https://www.douyu.com/"));
            Check(passport == "LTP0=synthetic-sso" && business == "acf_auth=synthetic-business", "SSO和网页业务凭据均保留且各自只发原域");
            Check(LoginState(ReviewCall(health, "Get", "douyu")) == "valid", "扫码确认结果立即关联新保存凭据，状态无需等待或旧缓存");
            transport.SetValue(null, LookupMakeDelegate(transport.FieldType, delegate { return HealthReply(health, 200, "{\"error\":16,\"data\":{}}"); }));
            object partial = NewRecordList(NewRecord("acf_auth", "synthetic-business", "www.douyu.com", "/", true, true, 0));
            Check(LoginState(ReviewCall(health, "CheckRecords", "douyu", partial)) == "unknown", "只有业务Cookie而缺少SSO时不误判网页账号失效");
            Check(LoginState(ReviewCall(health, "CheckRecords", "douyu", records)) == "invalid", "完整会话被官方明确拒绝仍正确报告失效");
        }
        finally { transport.SetValue(null, old); runtime.SetValue(null, oldRuntime); Directory.Delete(dir, true); }
    }
    private static object CookieTask(object records)
    {
        foreach (MethodInfo method in typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (method.Name == "FromResult" && method.IsGenericMethodDefinition)
                return method.MakeGenericMethod(records.GetType()).Invoke(null, new[] { records });
        throw new Exception("Task.FromResult缺失");
    }
    private static object NewLoginWindow()
    {
        Type type = DanmakuType("PlatformLoginBrowser").GetNestedType("BrowserWindow", BindingFlags.NonPublic);
        object platform = ReviewCall(DanmakuType("PlatformLogin"), "Get", "douyu");
        return Activator.CreateInstance(type, ReviewInstance, null, new object[] { platform, false }, null);
    }
    private static object ValidLoginResult()
    {
        object result = Activator.CreateInstance(DanmakuType("LoginHealth").GetNestedType("Result", BindingFlags.NonPublic));
        LookupSet(result, "State", "valid"); LookupSet(result, "Reason", "synthetic verified"); return result;
    }
    private static void TestLoginBrowserInteraction()
    {
        Exception failure = null; var thread = new Thread(delegate()
        {
            var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
            try
            {
                int writes = 0, beats = 0;
                object records = NewRecordList(NewRecord("acf_auth", "synthetic-A", "www.douyu.com", "/", true, true, 0));
                using (Form form = (Form)NewLoginWindow())
                using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    LookupDelegate(form, "ReadCookies", delegate { return CookieTask(records); });
                    LookupDelegate(form, "Verify", delegate { entered.Set(); release.WaitOne(3000); return ValidLoginResult(); });
                    LookupDelegate(form, "Persist", delegate { writes++; return true; });
                    form.StartPosition = FormStartPosition.Manual; form.Left = -20000; form.ShowInTaskbar = false; form.Show();
                    timer.Tick += delegate { beats++; }; timer.Start();
                    Task poll = (Task)LookupCall(form, "Poll", true);
                    Check(entered.WaitOne(1000), "慢检测确实进入后台网络线程");
                    LookupPumpUntil(delegate { return beats >= 3; }, 1000);
                    Check(beats >= 3, "后台请求等待时UI计时器仍持续工作，没有DoEvents/Sleep阻塞循环");
                    Button close = (Button)LookupGet(form, "CloseButton");
                    Check(close.Enabled && !form.TopMost, "关闭按钮可用且窗口不置顶遮挡其他浏览器");
                    close.PerformClick(); release.Set();
                    LookupPumpUntil(delegate { return poll.IsCompleted; }, 1000);
                    Check(poll.IsCompleted && writes == 0 && form.IsDisposed, "关闭按钮实际可点击，迟到的验证结果不能保存或重新弹窗");
                }
                using (Form form = (Form)NewLoginWindow())
                {
                    object current = records; entered.Reset(); release.Reset();
                    LookupDelegate(form, "ReadCookies", delegate { return CookieTask(current); });
                    LookupDelegate(form, "Verify", delegate { entered.Set(); release.WaitOne(3000); return ValidLoginResult(); });
                    LookupDelegate(form, "Persist", delegate { writes++; return true; });
                    form.StartPosition = FormStartPosition.Manual; form.Left = -20000; form.ShowInTaskbar = false; form.Show();
                    Task poll = (Task)LookupCall(form, "Poll", true); Check(entered.WaitOne(1000), "账号A验证已开始");
                    current = NewRecordList(NewRecord("acf_auth", "synthetic-B", "www.douyu.com", "/", true, true, 0)); release.Set();
                    LookupPumpUntil(delegate { return poll.IsCompleted; }, 1000);
                    Check(writes == 0 && !form.IsDisposed, "等待期间切换账号，不保存账号A的旧响应");
                    Task again = (Task)LookupCall(form, "Poll", true);
                    LookupPumpUntil(delegate { return again.IsCompleted; }, 1000);
                    Check(writes == 1 && (bool)LookupGet(form, "Saved"), "账号B确认后自动保存并返回，无需我已完成按钮");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { release.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(12000)) throw new Exception("登录窗口交互回归超时");
        if (failure != null) throw failure;
    }
    private static void TestLoginBrowserLayout()
    {
        Type type = DanmakuType("PlatformLoginBrowser");
        Check((bool)ReviewCall(type, "AllowedPage", "douyu", "https://passport.douyu.com/member/login"), "允许本平台登录子域");
        Check(!(bool)ReviewCall(type, "AllowedPage", "douyu", "https://douyu.com.evil.example/"), "拒绝仿冒登录域");
        Check(!(bool)ReviewCall(type, "AllowedPage", "douyu", "https://www.huya.com/"), "拒绝把其他平台混入本次登录");
        Exception failure = null; var thread = new Thread(delegate()
        {
            try
            {
                using (Form form = (Form)NewLoginWindow())
                {
                    foreach (int dpi in new[] { 96, 144, 192, 240 })
                    {
                        form.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(form, new object[] { dpi });
                        form.ClientSize = new System.Drawing.Size(640 * dpi / 96, 480 * dpi / 96); form.PerformLayout();
                        var page = form.Controls.Find("loginWebPage", true)[0];
                        var close = form.Controls.Find("loginBrowserClose", true)[0];
                        Check(page.Width > 0 && page.Height >= 150 * dpi / 96, "DPI=" + dpi + " 官网占据主体空间");
                        Check(close.Parent.ClientRectangle.Contains(close.Bounds), "DPI=" + dpi + " 关闭按钮完整可见");
                        Check(form.Controls.Find("loginBrowserRetry", true).Length == 1 && form.Controls.Find("loginBrowserSavePending", true).Length == 1,
                            "DPI=" + dpi + " 重试与待验证保存均在同一窗口内");
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(10000)) throw new Exception("登录布局回归超时"); if (failure != null) throw failure;
    }
}
