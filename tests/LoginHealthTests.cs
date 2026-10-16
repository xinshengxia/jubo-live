using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterLoginHealthCases()
    {
        TestRunner.Add("登录状态", "四平台明确登录证据与网络/风控不混淆", TestLoginEvidence);
        TestRunner.Add("登录状态", "本地过期、作用域与游客Cookie不冒充登录", TestLoginLocalState);
        TestRunner.Add("登录状态", "凭据切换隔离、旧响应丢弃、保留上次成功与脱敏", TestLoginGeneration);
        TestRunner.Add("登录状态", "主界面入口实际打开详情且检测操作齐全", TestLoginMainEntry);
    }
    private static string LoginState(object result) { return (string)LookupGet(result, "State"); }
    private static void TestLoginEvidence()
    {
        Type health = DanmakuType("LoginHealth");
        string[] platforms = { "bili", "huya", "douyu", "douyin" };
        string[] accepted = {
            "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":123}}",
            "{\"isLogined\":true,\"uid\":123}",
            "livehubLoginCheck({\"error\":0,\"data\":{\"uid\":123}})",
            "{\"status_code\":0,\"user\":{\"uid\":\"123\"}}"
        };
        string[] rejected = {
            "{\"code\":-101,\"data\":{\"isLogin\":false}}",
            "{\"isLogined\":false,\"uid\":0}",
            "livehubLoginCheck({\"error\":16,\"msg\":\"未登录,请重新登录\",\"data\":{}})",
            "{\"status_code\":8,\"status_msg\":\"用户未登录\"}"
        };
        for (int i = 0; i < platforms.Length; i++)
        {
            string p = platforms[i];
            Check(LoginState(ReviewCall(health, "Evaluate", p, 200, accepted[i])) == "valid", p + "：平台明确接受才标有效");
            Check(LoginState(ReviewCall(health, "Evaluate", p, 200, rejected[i])) == "invalid", p + "：明确未登录才标失效");
            foreach (int status in new[] { 401, 403, 429, 302 })
                Check(LoginState(ReviewCall(health, "Evaluate", p, status, rejected[i])) == "restricted", p + "：HTTP限制不是凭据失效证据");
            Check(LoginState(ReviewCall(health, "Evaluate", p, 503, "")) == "network", p + "：服务异常不误判失效");
            Check(LoginState(ReviewCall(health, "Evaluate", p, 200, "")) == "restricted", p + "：空响应不误报有效");
            Check(LoginState(ReviewCall(health, "Evaluate", p, 200, "{\"code\":0,\"status_code\":0}")) == "unknown", p + "：通用成功码不等于已登录");
            Check(LoginState(ReviewCall(health, "Evaluate", p, 200, "<html>verify</html>")) == "unknown", p + "：网页或格式变化不误判失效");
            Check(((string)ReviewCall(health, "Endpoint", p)).StartsWith("https://"), p + "：只检查官方HTTPS地址");
        }
        Check(LoginState(ReviewCall(health, "Evaluate", "bili", 200, "{\"code\":0,\"data\":{\"isLogin\":true}}")) == "unknown", "缺少账号身份字段不能标有效");
        Check(LoginState(ReviewCall(health, "Evaluate", "douyin", 200, "{\"status_code\":8,\"status_msg\":\"需要验证\"}")) == "unknown", "抖音相同错误码但语义不同不误判过期");
    }
    private static void SaveLoginFixture(string path, string name, string value, string domain, long expires)
    {
        File.WriteAllText(path, "# Netscape HTTP Cookie File\n" + domain + "\tTRUE\t/\tTRUE\t" + expires + "\t" + name + "\t" + value + "\n");
        ReviewCall(DanmakuType("CredentialCookies"), "Save", path,
            NewRecordList(NewRecord(name, value, domain, "/", true, true, expires)));
    }
    private static void TestLoginLocalState()
    {
        Type health = DanmakuType("LoginHealth");
        FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic), cookie = hub.GetField("Cookie", ReviewStatic);
        object oldRuntime = runtime.GetValue(null), oldCookie = cookie.GetValue(null); string dir = ReviewDirectory();
        try
        {
            runtime.SetValue(null, dir); string path = Path.Combine(dir, "bili-cookies.txt"); cookie.SetValue(null, path);
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "missing", "无凭据时显示未保存");
            File.WriteAllText(path, "SESSDATA=unknown-scope");
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "metadata", "无作用域裸串要求重新登录");
            SaveLoginFixture(path, "buvid3", "visitor", ".bilibili.com", 4102444800L);
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "missing", "游客Cookie不冒充登录凭据");
            SaveLoginFixture(path, "SESSDATA", "synthetic", ".evil.example", 4102444800L);
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "missing", "不属于平台的同名凭据无效");
            SaveLoginFixture(path, "SESSDATA", "synthetic", ".bilibili.com", 1);
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "expired", "关键登录Cookie明确到期单独显示");
            SaveLoginFixture(path, "SESSDATA", "synthetic", ".bilibili.com", 0);
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "saved", "会话Cookie没有到期时间也须在线验证");
            File.Delete(path);
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "missing", "B站播放Cookie已删除时，残留旁文件不能冒充可用凭据");
            File.WriteAllText(path, "# Netscape HTTP Cookie File\n.bilibili.com\tTRUE\t/\tTRUE\t4102444800\tSESSDATA\traw-current\n");
            object local = ReviewCall(health, "ReadLocal", "bili");
            var records = (System.Collections.IList)LookupGet(local, "Records");
            Check((string)LookupGet(records[0], "Value") == "raw-current", "B站检查使用播放器实际读取的Netscape值，不采用旧旁文件值");
            File.Delete(path + ".json");
            File.WriteAllText(path, "# Netscape HTTP Cookie File\n.bilibili.com\tTRUE\t/\tTRUE\t4102444800\tSESSDATA\tsynthetic\n");
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "saved", "旧B站Netscape文件保留真实作用域可检查");
            string dy = Path.Combine(dir, "douyu-cookies.txt");
            File.WriteAllText(dy, "dy_auth=unknown-scope");
            Check(LoginState(ReviewCall(health, "Get", "douyu")) == "metadata", "斗鱼裸Cookie不能杜撰作用域后发送");
        }
        finally { runtime.SetValue(null, oldRuntime); cookie.SetValue(null, oldCookie); Directory.Delete(dir, true); }
    }
    private static object HealthReply(Type health, int status, string body)
    {
        object reply = Activator.CreateInstance(health.GetNestedType("Reply", ReviewInstance | BindingFlags.Public));
        LookupSet(reply, "Status", status); LookupSet(reply, "Body", body); return reply;
    }
    private static void TestLoginGeneration()
    {
        Type health = DanmakuType("LoginHealth");
        FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic), cookie = hub.GetField("Cookie", ReviewStatic), transport = health.GetField("Transport", ReviewStatic);
        object oldRuntime = runtime.GetValue(null), oldCookie = cookie.GetValue(null), oldTransport = transport.GetValue(null);
        string dir = ReviewDirectory(), path = Path.Combine(dir, "bili-cookies.txt"), resultPath = Path.Combine(dir, "health-login-bili.json");
        try
        {
            runtime.SetValue(null, dir); cookie.SetValue(null, path);
            SaveLoginFixture(path, "SESSDATA", "fixture-secret-A", ".bilibili.com", 4102444800L);
            transport.SetValue(null, LookupMakeDelegate(transport.FieldType, delegate {
                return HealthReply(health, 200, "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":123}}"); }));
            ReviewCall(health, "CheckAndSave", "bili", ReviewCall(health, "ReadLocal", "bili"));
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "valid", "实际检测结果关联当前凭据显示有效");
            Check(!File.ReadAllText(resultPath).Contains("fixture-secret") && !File.ReadAllText(resultPath).Contains("\"mid\"")
                && !File.ReadAllText(resultPath).Contains("\"uid\""), "持久化结果不含Cookie值或账号编号字段");
            transport.SetValue(null, LookupMakeDelegate(transport.FieldType, delegate { throw new TimeoutException(); }));
            ReviewCall(health, "CheckAndSave", "bili", ReviewCall(health, "ReadLocal", "bili"));
            object network = ReviewCall(health, "Get", "bili");
            Check(LoginState(network) == "network" && (long)LookupGet(network, "VerifiedUtc") > 0, "网络失败显示无法确认并保留上次明确有效时间");
            var stored = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(resultPath));
            stored["State"] = "valid"; stored["CheckedUtc"] = DateTime.UtcNow.AddMinutes(-11).Ticks;
            File.WriteAllText(resultPath, new JavaScriptSerializer().Serialize(stored));
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "stale", "超过10分钟旧成功不继续亮绿色");
            transport.SetValue(null, LookupMakeDelegate(transport.FieldType, delegate {
                SaveLoginFixture(path, "SESSDATA", "fixture-secret-B", ".bilibili.com", 4102444800L);
                return HealthReply(health, 200, "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":123}}"); }));
            ReviewCall(health, "CheckAndSave", "bili", ReviewCall(health, "ReadLocal", "bili"));
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "saved", "请求途中更换Cookie：丢弃旧响应且要求新凭据重新验证");
            File.Delete(path); File.Delete(path + ".json");
            Check(LoginState(ReviewCall(health, "Get", "bili")) == "missing", "退出登录不复活旧成功状态");
        }
        finally { transport.SetValue(null, oldTransport); runtime.SetValue(null, oldRuntime); cookie.SetValue(null, oldCookie); Directory.Delete(dir, true); }
    }

    private static void TestLoginMainEntry()
    {
        Exception failure = null;
        var thread = new Thread(delegate()
        {
            string dir = ReviewDirectory();
            FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic), cookie = hub.GetField("Cookie", ReviewStatic);
            object oldRuntime = runtime.GetValue(null), oldCookie = cookie.GetValue(null);
            try
            {
                runtime.SetValue(null, dir); cookie.SetValue(null, Path.Combine(dir, "absent-cookie.txt"));
                using (Form launcher = (Form)hub.GetMethod("BuildLauncherForm", ReviewStatic, null, new[] { typeof(int) }, null).Invoke(null, new object[] { -1 }))
                using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
                {
                    launcher.StartPosition = FormStartPosition.Manual; launcher.Left = -20000; launcher.ShowInTaskbar = false;
                    launcher.Show(); Application.DoEvents();
                    bool found = false;
                    timer.Tick += delegate
                    {
                        Form detail = null;
                        foreach (Form form in Application.OpenForms) if (form.Text == "本地数据与平台状态") detail = form;
                        if (detail == null) return;
                        timer.Stop();
                        try
                        {
                            found = true;
                            foreach (string name in new[] { "checkLogin", "checkAllLogins", "loginAgain", "logout", "clean" })
                                Check(detail.Controls.Find(name, true).Length == 1, "真实详情窗口提供操作：" + name);
                            TextBox info = (TextBox)detail.Controls.Find("info", true)[0];
                            Check(info.Text.Contains("Cookie / 登录状态") && info.Text.Contains("尚未在线验证"), "真实打开事件展示独立Cookie状态与验证时间");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { detail.Close(); }
                    };
                    timer.Start(); ((Button)launcher.Controls.Find("localDataEntry", true)[0]).PerformClick();
                    timer.Stop(); Check(found, "主界面按钮实际打开详情，而非只有控件名称");
                    launcher.Close();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { runtime.SetValue(null, oldRuntime); cookie.SetValue(null, oldCookie); Directory.Delete(dir, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(10000)) throw new Exception("主界面登录详情回归超时");
        if (failure != null) throw failure;
    }
}
