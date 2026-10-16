using System;
using System.Drawing;
using System.Windows.Forms;

internal static partial class LiveHub
{
    private static void ShowRoomInputHelp(Form owner)
    {
        using (Form dialog = BuildRoomInputHelpForm()) dialog.ShowDialog(owner);
    }

    private static Form BuildRoomInputHelpForm()
    {
        var dialog = new DpiForm();
        dialog.Name = "roomInputHelpDialog";
        dialog.Text = "房间号 / 链接格式说明";
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.ClientSize = new Size(DpiLayout.S(700), DpiLayout.S(480));
        dialog.MinimumSize = new Size(DpiLayout.S(460), DpiLayout.S(320));
        dialog.MinimizeBox = false;
        dialog.MaximizeBox = false;
        dialog.BackColor = ModernTheme.Surface;
        dialog.ForeColor = ModernTheme.Text;
        dialog.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(dialog); };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(DpiLayout.S(12)) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiLayout.S(44)));
        var content = new ModernInput(true) { Name = "roomInputHelpContent", Dock = DockStyle.Fill, Margin = Padding.Empty };
        content.Inner.ReadOnly = true;
        content.Inner.WordWrap = true;
        content.Inner.ScrollBars = ScrollBars.Vertical;
        content.Text = "直接输入房间号时，先选择对应平台；带平台前缀或完整链接时，会自动识别平台。\r\n"
            + "链接请包含 https://，可保留 ? 后的分享参数。以下号码仅作格式示例。\r\n\r\n"
            + "【B站】数字直播房间号（含短号）\r\n"
            + "7777  或  bili:7777\r\n"
            + "https://live.bilibili.com/7777\r\n"
            + "https://live.bilibili.com/blanc/7777\r\n\r\n"
            + "【虎牙】数字房间号或房间别名（字母、数字、下划线、连字符）\r\n"
            + "910323  或  huya:910323  或  clkimmy  或  huya:a16789\r\n"
            + "https://www.huya.com/910323\r\n"
            + "https://www.huya.com/clkimmy\r\n"
            + "也支持 https://huya.com/ 后接房间号或别名。\r\n\r\n"
            + "【斗鱼】房间号或房间别名（字母、数字、下划线、连字符）\r\n"
            + "952595  或  douyu:952595\r\n"
            + "https://www.douyu.com/952595\r\n"
            + "https://m.douyu.com/952595\r\n"
            + "也支持 https://douyu.com/ 后接房间号或别名。\r\n\r\n"
            + "【抖音】直播网页中的数字房间号\r\n"
            + "723910165684  或  douyin:723910165684\r\n"
            + "https://live.douyin.com/723910165684\r\n"
            + "也支持 https://www.douyin.com/ 后接数字房间号。\r\n\r\n"
            + "【昵称、账号主页与分享短链】\r\n"
            + "分享短链（如 b23.tv / v.douyin.com）请先在浏览器打开，再复制上述完整直播间链接。\r\n"
            + "主播昵称、抖音号、账号主页或整段分享文案，请使用主界面的「查找主播」进行查询与核验。";
        layout.Controls.Add(content, 0, 0);
        var close = new ModernButton { Text = "知道了", Name = "roomInputHelpClose", Dock = DockStyle.Right,
            Width = DpiLayout.S(88), Margin = new Padding(0, DpiLayout.S(10), 0, 0), DialogResult = DialogResult.OK };
        layout.Controls.Add(close, 0, 1);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.Shown += delegate { content.Inner.Select(0, 0); close.Focus(); };
        dialog.Controls.Add(layout);
        return dialog;
    }
}
