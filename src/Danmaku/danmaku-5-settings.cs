// --------------------------------------------------------------------------------------------
// 2026-09-25：单文件约束解除后本文件**独立参与编译**，所以自带 using。
// （在此之前这些模块是被生成器拼进 live-hub-v2.cs 的，靠宿主文件的 using 才编得过。）
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
// --------------------------------------------------------------------------------------------
// 设置对话框（启动器里点「弹幕设置…」打开）
// 改完直接写 danmaku.json —— 已经在跑的弹幕进程每秒检查一次 mtime，会自动热重载。
//
// 布局上踩过的坑（值得记）：
//   最早把窗体高度写死 620、两个按钮钉死在 y=578。后来一轮轮往里加选项（显示区域 / 防挡字幕 /
//   点 SC 面板 / 始终最前…），内容长到 **864px**，而屏幕逻辑高度只有 **800** ——
//   结果最后几个复选框和「保存」按钮全被挤出可视区（用户直接看到了这个 bug）。
//   所以现在：
//     ① **高度按内容算**，不写死；
//     ② 内容放进**可滚动面板**，两个按钮**钉在窗体底部** —— 内容再怎么长，按钮一定够得到；
//     ③ 选项改成**双列**，让它真的能在 800 高的屏上放下（不用滚动）；
//     ④ 拆出 `Build()` 供测试渲染并断言「所有控件都在可视区内」，这类 bug 不再靠肉眼发现。
// --------------------------------------------------------------------------------------------

/// Build() 的产物：测试要拿它断言布局，Show() 要拿它显示
internal sealed class SettingsDialogParts
{
    public Form Dialog;
    public Panel Body;
    public Button Ok;
    public Button Cancel;
}

internal sealed class DanmakuSettingsDialog
{
    private const int DialogWidth = 700;
    private const int Margin = 16;
    private const int ColumnWidth = 330;
    private const int RowHeight = 28;          // 下拉行
    private const int CheckHeight = 26;        // 复选框行
    private const int ButtonStripHeight = 48;  // 底部按钮条

    public static void Show(IWin32Window owner)
    {
        DanmakuConfig config = DanmakuHost.LoadOrCreateConfig();
        SettingsDialogParts parts = Build(config);
        if (parts.Dialog.ShowDialog(owner) != DialogResult.OK) return;
        // 保存逻辑挂在「保存」按钮的 Click 上（见 Build 里的 save 委托），
        // 所以这里不用再把控件值读一遍 —— Cancel 不会触发它。
    }

    /// 把对话框搭出来但**不显示** —— 测试会渲染它并断言所有控件都在可视区内。
    /// 把对话框搭出来但不显示（--settings-probe / 离线回归用它验布局与配色）。
    internal static SettingsDialogParts Build(DanmakuConfig config)
    {
        Form dialog = new DpiForm();
        dialog.Text = "弹幕与 SC 设置";
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.MinimizeBox = false;
        dialog.MaximizeBox = false;
        dialog.StartPosition = FormStartPosition.CenterParent;
        // 现代化外观：统一字体 + 浅色底。控件都是 danmaku-15-ui.cs 里的自绘版本。
        // ⚠️ 位置一个字没动 —— 离线测试断言「内容恰好放得下（needed == 可视）」，实测都是 530，
        //    没有余量。改布局得先解决高度，那是另一件事。
        DpiLayout.SetDesignFont(dialog, ModernTheme.UiFont);
        dialog.BackColor = ModernTheme.Surface;
        dialog.ForeColor = ModernTheme.Text;
        dialog.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(dialog); };
        // ⚠️ 这一行**改过**：原来写的是 AutoScaleMode.None（拒绝一切缩放），
        //    因为那时进程**不感知 DPI**，Windows 会把整个窗口位图放大 2 倍，
        //    硬编码的像素坐标「看起来」正好 —— 代价是文字全糊。
        //    现在进程声明了 DPI 感知（DpiAwareness.Enable），位图放大没有了，
        //    必须自己按 DPI 缩放布局，否则 200% 屏上这个对话框只有一半大小。
        //    ⚠️ 但**赋值点必须挪到最后**（见 return 之前）—— AutoScaleMode 一赋值就立刻
        //    触发 PerformAutoScale，那时还没子控件，等于没缩且标记成「已缩放」。

        // 内容放可滚动面板；按钮放面板**外面**（钉在窗体底部，永远可见）
        Panel body = new Panel();
        body.SetBounds(0, 0, DialogWidth, 100);
        body.AutoScroll = true;
        body.BackColor = ModernTheme.Surface;
        dialog.Controls.Add(body);
        // ⚠️ 容器底色的**兜底订阅**：自绘控件各自订阅了 ModernTheme.Changed，但 dialog / body
        //    这两个容器原来只靠「勾选框那个 handler 顺手刷一下」—— 主题从别的路径变了就没它们的事，
        //    而 body 的底色还**被子控件继承**（ModernSectionBar 那些竖条会一起留在浅色）。
        //    2026-09-27：改为自己订阅，Dispose 时退订（静态事件不放手会把关掉的窗口一直留在内存里）。
        Action followTheme = delegate
        {
            dialog.BackColor = ModernTheme.Surface;
            body.BackColor = ModernTheme.Surface;
            dialog.ForeColor = ModernTheme.Text;
        };
        ModernTheme.Changed += followTheme;
        dialog.Disposed += delegate { ModernTheme.Changed -= followTheme; };

        int leftColumn = Margin;
        int rightColumn = Margin + ColumnWidth + 10;
        int y = 14;

        // 分组标题：加粗文字 + 左边一条强调色竖条。返回下一块内容的 y。
        // ⚠️ 高度预算：屏限高 screenLimit = 工作区高 - 80（本机 800 逻辑像素 → 720）。
        //    原来内容 544 + 按钮条 48 = 592；三个标题 + 一个复选框共 +110 → 702，还放得下。
        //    再加东西就会触发滚动，而离线测试断言「内容不用滚动就能放下」。
        Func<string, int, int> section = delegate (string title, int top)
        {
            ModernSectionBar bar = new ModernSectionBar();
            bar.SetBounds(leftColumn - 8, top + 4, 3, 15);
            body.Controls.Add(bar);
            Label label = ModernTheme.SectionHeader(title);
            label.SetBounds(leftColumn, top, 320, 22);
            body.Controls.Add(label);
            return top + 26;
        };

        y = section("显示与内容", y);

        // ---------------- 下拉选项（双列） ----------------
        int comboRow = y;
        int column = 0;
        Func<string, ComboBox> combo = delegate (string label)
        {
            int x = column == 0 ? leftColumn : rightColumn;
            Label caption = new ModernLabel();
            caption.Text = label;
            DpiLayout.SetDesignFont(caption, ModernTheme.UiFont);
            caption.ForeColor = ModernTheme.TextSecondary;
            caption.BackColor = Color.Transparent;
            caption.SetBounds(x, comboRow + 4, 120, 22);
            body.Controls.Add(caption);

            ComboBox box = new ModernComboBox();
            box.SetBounds(x + 122, comboRow, ColumnWidth - 122, 26);
            body.Controls.Add(box);

            column++;
            if (column == 2) { column = 0; comboRow += RowHeight; }
            return box;
        };

        ComboBox fontScale = combo("弹幕字号");
        int[] fontValues = new int[] { 60, 80, 100, 130, 170 };
        string[] fontLabels = new string[] { "超小", "小", "标准（同 B站）", "大", "超大" };
        for (int i = 0; i < fontValues.Length; i++) fontScale.Items.Add(fontLabels[i]);
        fontScale.SelectedIndex = Math.Max(0, Array.IndexOf(fontValues, config.FontScalePercent));

        ComboBox speed = combo("弹幕速度");
        int[] speedValues = new int[] { 60, 80, 100, 130, 170 };
        string[] speedLabels = new string[] { "很慢（13 秒）", "慢（10 秒）", "标准（8 秒）", "快（6 秒）", "很快（4.7 秒）" };
        for (int i = 0; i < speedValues.Length; i++) speed.Items.Add(speedLabels[i]);
        speed.SelectedIndex = Math.Max(0, Array.IndexOf(speedValues, config.SpeedPercent));

        ComboBox opacity = combo("不透明度");
        int[] opacityValues = new int[] { 100, 75, 50, 25 };
        for (int i = 0; i < opacityValues.Length; i++) opacity.Items.Add(opacityValues[i] + "%");
        opacity.SelectedIndex = Math.Max(0, Array.IndexOf(opacityValues, config.OpacityPercent));

        ComboBox area = combo("显示区域");
        int[] areaValues = new int[] { 100, 75, 50, 25 };
        string[] areaLabels = new string[] { "全屏", "3/4 屏", "半屏", "1/4 屏" };
        for (int i = 0; i < areaValues.Length; i++) area.Items.Add(areaLabels[i]);
        area.SelectedIndex = Math.Max(0, Array.IndexOf(areaValues, config.DisplayAreaPercent));

        // 弹幕延迟补偿：弹幕是实时到的，视频是缓冲后才显示的 —— 不补偿就领先画面
        // 2.5~4 秒，等于剧透（先看到「哈哈哈他没了」，几秒后才看到画面里没）。
        // 选项直接给**各平台实测的缓冲深度**，不用用户盲调。
        ComboBox delay = combo("弹幕延迟补偿");
        int[] delayValues = new int[] { 0, 2000, 2500, 3000, 3500, 4000, 5000 };
        string[] delayLabels = new string[] {
            "关闭（不补偿）",
            "2.0 秒",
            "2.5 秒  ← 虎牙/斗鱼实测缓冲",
            "3.0 秒",
            "3.5 秒  ← B站实测缓冲",
            "4.0 秒  ← 抖音实测缓冲",
            "5.0 秒（画面本来就慢时）" };
        for (int i = 0; i < delayValues.Length; i++) delay.Items.Add(delayLabels[i]);
        delay.SelectedIndex = Math.Max(0, Array.IndexOf(delayValues, config.DanmakuDelayMs));
        if (delay.SelectedIndex <= 0 && config.DanmakuDelayMs > 0) delay.SelectedIndex = 0;   // 非预设值归到「关闭」免得错位

        ComboBox density = combo("弹幕密度");
        int[] densityValues = new int[] { 400, 150, 80, 40 };
        string[] densityLabels = new string[] { "不限", "正常", "较少", "最少" };
        for (int i = 0; i < densityValues.Length; i++) density.Items.Add(densityLabels[i]);
        density.SelectedIndex = Math.Max(0, Array.IndexOf(densityValues, config.MaxOnScreen));

        ComboBox duplicates = combo("重复弹幕");
        duplicates.Name = "DuplicateMode";
        duplicates.Items.AddRange(new object[] { "逐条显示", "仅合并计数", "合并并适度放大" });
        duplicates.SelectedIndex = !config.BlockDuplicate ? 0 : config.EnlargeMergedDanmaku ? 2 : 1;
        ComboBox duplicateWindow = combo("合并时间");
        duplicateWindow.Name = "DuplicateWindow";
        int[] duplicateWindows = new int[] { 3000, 5000, 8000, 10000 };
        foreach (int milliseconds in duplicateWindows) duplicateWindow.Items.Add((milliseconds / 1000) + " 秒");
        int duplicateWindowIndex = Array.IndexOf(duplicateWindows, config.DuplicateWindowMs);
        if (duplicateWindowIndex < 0)
        {
            duplicateWindow.Items.Add((config.DuplicateWindowMs / 1000.0).ToString("0.###") + " 秒（自定义）");
            duplicateWindowIndex = duplicateWindow.Items.Count - 1;
        }
        duplicateWindow.SelectedIndex = duplicateWindowIndex;

        ComboBox fontName = combo("字体");
        // 「鸿蒙」只在字体真的随包带上时才出现 —— 没带却给选项的话，
        // GDI+ 会**静默**换成默认字体，用户会以为「选了没反应」。
        string[] fontValues2 = UiFonts.Available
            ? new string[] { "鸿蒙", "微软雅黑", "黑体", "宋体" }
            : new string[] { "微软雅黑", "黑体", "宋体" };
        for (int i = 0; i < fontValues2.Length; i++) fontName.Items.Add(fontValues2[i]);
        fontName.SelectedIndex = Math.Max(0, Array.IndexOf(fontValues2, config.FontName));

        ComboBox scMode = combo("SC 醒目留言");
        string[] scValues = new string[] { "full", "compact", "badge", "off" };
        string[] scLabels = new string[] { "完整卡片", "折叠成一行", "只留角标", "不显示" };
        for (int i = 0; i < scValues.Length; i++) scMode.Items.Add(scLabels[i]);
        scMode.SelectedIndex = Math.Max(0, Array.IndexOf(scValues, config.ScMode));

        ComboBox scStack = combo("超过几张启用滚动");
        // ⚠️ 2026-09-28 语义变更：这一档原来叫「SC 最多几张」、作用是**淘汰上限**
        //    （超过就丢最旧的）。用户澄清了他的设计意图：
        //      **≤ N 张正常堆叠；超过 N 张一张都不丢，启用滚轮翻看。**
        //    所以标签改成「超过几张启用滚动」，渲染端也不再按它淘汰
        //    （只留一个不暴露给用户的 200 硬顶，见 SuperChatHardCap）。
        // 0 = 不限：不按张数收窄可视区，只在**真的放不下**时才滚。
        int[] stackValues = new int[] { 1, 2, 3, 4, 6, 12, 0 };
        string[] stackLabels = new string[] { "超过 1 张", "超过 2 张", "超过 3 张", "超过 4 张", "超过 6 张", "超过 12 张", "不限（放不下才滚）" };
        for (int i = 0; i < stackValues.Length; i++) scStack.Items.Add(stackLabels[i]);
        // ⚠️ 2026-09-28 修：原来直接 `Math.Max(0, Array.IndexOf(...))` ——
        //    配置里的值不在选项里时 IndexOf 返回 -1，被 Math.Max 抬成 0，
        //    于是**静默显示成第一项「1 张」**。配合当时 Normalize 把 12 夹成 8、
        //    把「不限」夹成 1，用户看到的就是「选 12 或不限，保存后打开还是 1」。
        //    现在：值不在选项里时挑**最接近**的档位，并在提示里说明修正过。
        int stackIndex = Array.IndexOf(stackValues, config.ScMaxStack);
        string stackFixNote = "";
        if (stackIndex < 0)
        {
            int best = 0, bestDiff = int.MaxValue;
            for (int i = 0; i < stackValues.Length; i++)
            {
                // 「不限」(0) 是个特殊档：只有正好等于 0 时才选它，否则按数值最近挑
                if (stackValues[i] == 0) continue;
                int diff = Math.Abs(stackValues[i] - config.ScMaxStack);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            stackIndex = best;
            stackFixNote = "（原值 " + config.ScMaxStack + " 不在选项里，已按最接近的 "
                + stackLabels[best] + " 显示）";
        }
        scStack.SelectedIndex = stackIndex;
        if (stackFixNote.Length > 0) scStack.Items[stackIndex] = stackLabels[stackIndex] + stackFixNote;

        ComboBox scCorner = combo("SC 贴哪个角");
        string[] cornerValues = new string[] { "tr", "br", "tl", "bl" };
        string[] cornerLabels = new string[] { "右上（同 B站）", "右下", "左上", "左下" };
        for (int i = 0; i < cornerValues.Length; i++) scCorner.Items.Add(cornerLabels[i]);
        scCorner.SelectedIndex = Math.Max(0, Array.IndexOf(cornerValues, config.ScCorner));

        // mpv 画质档位。⚠️ 必须和其它下拉框一起创建（在 comboRow 收尾之前）——
        // 放到复选框之后会让 combo() 往已结束的下拉区再追加一行，
        // 那一行正好落在复选框上、压住「屏蔽词」标签（用户截图报的遮挡）。
        ComboBox mpvQuality = combo("mpv 画质档位");
        string[] pqValues = new string[] { "off", "light", "high" };
        string[] pqLabels = new string[] {
            "无（不增强）",
            "轻度（抗振铃，GPU 均 22% 几乎不涨）",
            "高质量（ewa_lanczossharp，GPU 均 47%）" };
        for (int i = 0; i < pqValues.Length; i++) mpvQuality.Items.Add(pqLabels[i]);
        mpvQuality.SelectedIndex = Math.Max(0, Array.IndexOf(pqValues, config.MpvQuality));
        if (column != 0) comboRow += RowHeight;      // 收尾：落了单的那一行
        y = comboRow + 10;

        // ---------------- 复选框（双列，顺序按「先常用后冷门」排） ----------------
        int checkTop = section("开关", y);
        column = 0;
        int checkRow = checkTop;
        Func<string, bool, CheckBox> check = delegate (string label, bool value)
        {
            int x = column == 0 ? leftColumn : rightColumn;
            CheckBox box = new ModernCheckBox();
            box.Text = label;
            box.Checked = value;
            box.SetBounds(x, checkRow, ColumnWidth, 24);
            body.Controls.Add(box);
            column++;
            if (column == 2) { column = 0; checkRow += CheckHeight; }
            return box;
        };

        CheckBox stroke = check("黑色描边（B站 的标志性观感）", config.StrokeEnabled);
        CheckBox medal = check("弹幕前显示粉丝勋章", config.ShowMedal);
        CheckBox userName = check("弹幕前显示用户名", config.ShowUserName);
        CheckBox userLevel = check("显示用户等级 / 舰长", config.ShowUserLevel);
        CheckBox emoticon = check("显示表情图片（B站 大表情）", config.ShowEmoticon);
        CheckBox autoStart = check("打开直播自动带起弹幕", config.AutoStart);
        CheckBox foreground = check("「始终最前」时非前台隐藏", config.HideWhenNotForeground);
        CheckBox alwaysOnTop = check("始终压在所有窗口之上", config.AlwaysOnTop);
        CheckBox scClickable = check("点 SC 面板切换形态", config.ScClickable);
        CheckBox antiSubtitle = check("防挡字幕（底部留 20%）", config.AntiBlockSubtitle);
        CheckBox scaleWithWindow = check("字号随画面大小缩放", config.ScaleWithWindow);
        CheckBox solidText = check("实心字芯（透明度仅影响 SC 背景）", config.SolidText);

        if (column != 0) checkRow += CheckHeight;
        y = checkRow + 12;

        // ---------------- 界面 ----------------
        y = section("界面", y);
        ModernCheckBox darkMode = new ModernCheckBox();
        darkMode.Text = "深色模式（启动器面板 + 本对话框）";
        darkMode.Checked = config.DarkMode;
        darkMode.SetBounds(leftColumn, y, ColumnWidth * 2 + 10, 24);
        body.Controls.Add(darkMode);
        // 立刻生效，不必等保存 —— 观感类开关要能当场看到效果
        darkMode.CheckedChanged += delegate
        {
            ModernTheme.Apply(darkMode.Checked);
            dialog.BackColor = ModernTheme.Surface;
            body.BackColor = ModernTheme.Surface;
            dialog.ForeColor = ModernTheme.Text;
            // 标题栏也要跟着切，否则深色窗体顶着一条亮白标题栏（2026-09-27 加）
            ModernTheme.ApplyWindowChrome(dialog);
            // 标签和自绘控件各自订阅了 ModernTheme.Changed，这里不用逐个刷
        };
        y += 26;   // 和下面的「屏蔽词」拉开距离（原 34，为了给 hint 的第四行腾高度收到 26）

        // ---------------- 屏蔽词 ----------------
        Label blockLabel = new ModernLabel();
        blockLabel.Text = "屏蔽词（每行一个；以 re: 开头按正则匹配）";
        DpiLayout.SetDesignFont(blockLabel, ModernTheme.UiFont);
        blockLabel.ForeColor = ModernTheme.TextSecondary;
        blockLabel.BackColor = Color.Transparent;
        blockLabel.SetBounds(leftColumn, y, 480, 22);
        body.Controls.Add(blockLabel);
        y += 24;

        // 圆角描边靠「无边框 TextBox 塞进自绘 Panel」实现 —— 见 ModernInput。
        ModernInput block = new ModernInput(true);
        block.SetBounds(leftColumn, y, DialogWidth - Margin * 2 - 20, 56);
        block.Text = string.Join(Environment.NewLine, config.BlockKeywords);
        body.Controls.Add(block);
        y += 68;

        Label hint = new ModernLabel();
        DpiLayout.SetDesignFont(hint, ModernTheme.UiFontSmall);
        hint.ForeColor = ModernTheme.TextSecondary;
        hint.BackColor = Color.Transparent;
        // ⚠️ 高度必须够放**四行**。原来给 34px 连三行都放不下（第三行一直被截，截图才发现）。
        //    第四行是 HarmonyOS Sans 的署名 —— 华为字体协议要求
        //    「YOU shall make a prominent notice in the software to state that
        //      HarmonyOS Sans Fonts are used」，这一行就是履行那一条。
        //    高度预算：屏限高 720；内容 664 + 按钮条 48 = 712 ✓ 留 8px 余量。
        hint.SetBounds(leftColumn, y, DialogWidth - Margin * 2 - 20, 64);
        hint.Text = "热键：Ctrl+Alt+D 开关弹幕；Ctrl+Alt+S 切换 SC 形态；Ctrl+Alt+O 隐藏覆盖层；\r\n"
            + "Ctrl+Alt+↑/↓ 调字号；Ctrl+Alt+←/→ 调显示区域；Ctrl+Alt+PgUp/PgDn 调速度；Ctrl+Alt+[ / ] 调弹幕延迟补偿。\r\n"
            + "也可以直接点画面上的 SC 面板切换形态。\r\n"
            + "界面字体：HarmonyOS Sans SC（华为免费字体，随包携带，许可见 fonts\\LICENSE.txt）";
        body.Controls.Add(hint);
        y += 64 + 8;

        // ---------------- 尺寸：按内容算，再受屏幕限制 ----------------
        int contentHeight = y;
        Screen screen = Screen.FromControl(dialog);
        int screenLimit = screen.WorkingArea.Height - 80;      // 给标题栏 / 任务栏留余量

        int wantedHeight = contentHeight + ButtonStripHeight;
        bool needsScroll = wantedHeight > screenLimit;
        int clientHeight = needsScroll ? screenLimit : wantedHeight;

        int bodyHeight = clientHeight - ButtonStripHeight;
        body.SetBounds(0, 0, DialogWidth, bodyHeight);
        body.AutoScroll = needsScroll;
        // 让滚动条按内容实际高度算（AutoScroll 靠这个）
        body.AutoScrollMinSize = new Size(0, contentHeight);

        // ---------------- 按钮：钉在窗体底部，**不再写死坐标** ----------------
        int buttonTop = clientHeight - ButtonStripHeight + 9;
        Button features = new ModernButton(); features.Text = "更多弹幕设置…";
        features.SetBounds(Margin, buttonTop, 160, 30);
        features.Click += delegate { DanmakuFeatureSettings.Show(dialog, config); };
        dialog.Controls.Add(features);
        ModernButton ok = new ModernButton();
        ok.Text = "保存";
        ok.Style = ModernButton.Kind.Primary;
        ok.DialogResult = DialogResult.OK;
        ok.SetBounds(DialogWidth - Margin - 88 - 96, buttonTop, 88, 30);
        dialog.Controls.Add(ok);

        ModernButton cancel = new ModernButton();
        cancel.Text = "取消";
        cancel.Style = ModernButton.Kind.Secondary;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(DialogWidth - Margin - 88, buttonTop, 88, 30);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        // ⚠️ 窗体尺寸要乘 DPI（下面所有坐标都是 96 DPI 的逻辑值）。
        //    控件树的缩放统一在 return 之前由 DpiLayout.ScaleTree 做。
        dialog.ClientSize = new Size(DpiLayout.S(DialogWidth), DpiLayout.S(clientHeight));
        body.AutoScrollMinSize = new Size(0, DpiLayout.S(contentHeight));

        // ---------------- 保存 ----------------
        Action save = delegate ()
        {
            config.FontScalePercent = fontValues[fontScale.SelectedIndex];
            config.SpeedPercent = speedValues[speed.SelectedIndex];
            config.OpacityPercent = opacityValues[opacity.SelectedIndex];
            config.DisplayAreaPercent = areaValues[area.SelectedIndex];
            config.MaxOnScreen = densityValues[density.SelectedIndex];
            config.DanmakuDelayMs = delayValues[delay.SelectedIndex];
            config.BlockDuplicate = duplicates.SelectedIndex != 0;
            config.EnlargeMergedDanmaku = duplicates.SelectedIndex == 2;
            if (duplicateWindow.SelectedIndex < duplicateWindows.Length)
                config.DuplicateWindowMs = duplicateWindows[duplicateWindow.SelectedIndex];
            config.ScMode = scValues[scMode.SelectedIndex];
            config.ScMaxStack = stackValues[scStack.SelectedIndex];
            config.ScCorner = cornerValues[scCorner.SelectedIndex];
            config.FontName = fontValues2[fontName.SelectedIndex];
            config.StrokeEnabled = stroke.Checked;
            config.ShowMedal = medal.Checked;
            config.ShowUserName = userName.Checked;
            config.ShowUserLevel = userLevel.Checked;
            config.ShowEmoticon = emoticon.Checked;
            config.AutoStart = autoStart.Checked;
            config.HideWhenNotForeground = foreground.Checked;
            config.AlwaysOnTop = alwaysOnTop.Checked;
            config.ScClickable = scClickable.Checked;
            config.AntiBlockSubtitle = antiSubtitle.Checked;
            config.ScaleWithWindow = scaleWithWindow.Checked;
            config.SolidText = solidText.Checked;
            config.MpvQuality = pqValues[mpvQuality.SelectedIndex];
            config.MpvEnhance = false;
            config.DarkMode = darkMode.Checked;
            config.BlockKeywords = block.Text.Split(new string[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);
            config.Normalize();
            if (!config.Save(DanmakuHost.ConfigPath()))
                MessageBox.Show(dialog, "设置没能保存（详见 runtime\\logs\\danmaku.log）。",
                    "看直播", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        ok.Click += delegate { save(); };

        // ⚠️ 这里**故意不设 AutoScaleMode** —— 见 DpiAwareness.Enable() 的说明：
        //    本项目所有窗体都是「硬编码像素 + 运行时按 ClientSize 动态算一部分」的混合布局，
        //    AutoScaleMode 缩放和它天生不兼容（实测控件被缩两次、跑到可视区外，
        //    底部按钮 rel y=2218 而客户区才 1120）。改用 UNAWARE_GDISCALED：
        //    布局交给系统位图缩放（像素坐标保持原样），文字由 GDI 按真实分辨率重绘。
        // ⚠️ DPI 缩放的唯一入口 —— 必须在所有控件（含 body 里的）都建完之后。
        //    只缩放 Bounds，不动 Font（字号是点值，本来就按设备 DPI 渲染）。
        //    不用 AutoScaleMode：它的触发时机和次数不受控，实测会缩两次。
        DpiLayout.ScaleTree(dialog);
        Dictionary<Control, Rectangle> designControls = new Dictionary<Control, Rectangle>();
        List<int> pairedRows = new List<int>();
        foreach (Control control in body.Controls)
        {
            designControls[control] = control.Bounds;
            if (control.Left >= rightColumn) pairedRows.Add(control.Top);
        }
        pairedRows.Sort();
        List<int> rowStarts = new List<int>();
        foreach (int top in pairedRows)
            if (rowStarts.Count == 0 || top - rowStarts[rowStarts.Count - 1] > 8) rowStarts.Add(top);
        bool reflowing = false;
        Action reflow = delegate
        {
            if (reflowing || ((DpiForm)dialog).ApplyingDpi) return;
            reflowing = true;
            int scrollY = -body.AutoScrollPosition.Y;
            body.SuspendLayout();
            try
            {
                body.AutoScrollPosition = Point.Empty;
                int strip = DpiLayout.S(dialog, ButtonStripHeight);
                int pad = DpiLayout.S(dialog, Margin);
                body.SetBounds(0, 0, dialog.ClientSize.Width, Math.Max(1, dialog.ClientSize.Height - strip));
                body.AutoScroll = true;
                bool singleColumn = dialog.ClientSize.Width < DpiLayout.S(dialog, DialogWidth - 40);
                int contentRight = Math.Max(pad + 1, body.Width - DpiLayout.ScrollbarWidth(dialog) - pad);
                int neededHeight = DpiLayout.S(dialog, contentHeight);
                foreach (KeyValuePair<Control, Rectangle> entry in designControls)
                {
                    Control control = entry.Key; Rectangle rect = entry.Value;
                    int extra = 0;
                    if (singleColumn)
                    {
                        foreach (int rowTop in rowStarts) if (rect.Top > rowTop + 8) extra += RowHeight;
                        if (rect.Left >= rightColumn) { rect.X -= rightColumn - leftColumn; extra += RowHeight; }
                    }
                    control.SetBounds(DpiLayout.S(dialog, rect.X), DpiLayout.S(dialog, rect.Y + extra),
                        DpiLayout.S(dialog, rect.Width), DpiLayout.S(dialog, rect.Height));
                    control.Width = Math.Max(1, Math.Min(control.Width, contentRight - control.Left));
                    if (control == hint)
                        control.Height = Math.Max(control.Height, TextRenderer.MeasureText(hint.Text, hint.Font,
                            new Size(control.Width, int.MaxValue), TextFormatFlags.WordBreak).Height + DpiLayout.S(dialog, 8));
                    neededHeight = Math.Max(neededHeight, control.Bottom + DpiLayout.S(dialog, 12));
                }
                body.AutoScrollMinSize = new Size(0, neededHeight);
                int top = dialog.ClientSize.Height - strip + DpiLayout.S(dialog, 9);
                cancel.SetBounds(dialog.ClientSize.Width - pad - DpiLayout.S(dialog, 88), top, DpiLayout.S(dialog, 88), DpiLayout.S(dialog, 30));
                ok.SetBounds(cancel.Left - DpiLayout.S(dialog, 96), top, DpiLayout.S(dialog, 88), DpiLayout.S(dialog, 30));
                features.SetBounds(pad, top, Math.Max(1, Math.Min(DpiLayout.S(dialog, 160), ok.Left - pad * 2)), DpiLayout.S(dialog, 30));
                }
            finally { body.ResumeLayout(true); reflowing = false; }
            body.AutoScrollPosition = new Point(0, scrollY);
        };

        ((DpiForm)dialog).DpiUpdated += delegate { reflow(); };
        body.ClientSizeChanged += delegate { reflow(); };
        body.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(body); };
        Action scrollTheme = delegate { ModernTheme.ApplyScrollbarTheme(body); };
        ModernTheme.Changed += scrollTheme;
        body.Disposed += delegate { ModernTheme.Changed -= scrollTheme; };

        SettingsDialogParts parts = new SettingsDialogParts();
        parts.Dialog = dialog;
        parts.Body = body;
        parts.Ok = ok;
        parts.Cancel = cancel;
        return parts;
    }
}
