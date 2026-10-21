using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 从主 partial 原样搬运；仍在同一 net48 编译单元。
internal static partial class LiveHub
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UiLogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, FontCharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Face;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr UiProbeSendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", CharSet = CharSet.Unicode)]
    private static extern int GetUiFontObject(IntPtr font, int size, out UiLogFont result);
    private static int NativeFontPixels(Control control)
    {
        IntPtr font = UiProbeSendMessage(control.Handle, 0x0031, IntPtr.Zero, IntPtr.Zero);
        UiLogFont value;
        if (font == IntPtr.Zero || GetUiFontObject(font, Marshal.SizeOf(typeof(UiLogFont)), out value) == 0) return 0;
        return Math.Abs(value.Height);
    }

    internal static void WriteUiProbe(Form form, string path)
    {
        DpiForm dpiForm = form as DpiForm;
        if (dpiForm != null) dpiForm.InitializeDpi();
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        CollectControls(form, form, rows, 0);
        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["clientWidth"] = form.ClientSize.Width;
        payload["clientHeight"] = form.ClientSize.Height;
        payload["minimumWidth"] = form.MinimumSize.Width;
        payload["minimumHeight"] = form.MinimumSize.Height;
        payload["dpiScale"] = DpiLayout.For(form);
        payload["windowDpi"] = DpiForm.WindowDpi(form);
        payload["osVersion"] = Environment.OSVersion.VersionString;
        payload["fontResourceAvailable"] = UiFonts.Available;
        payload["monitorCount"] = Screen.AllScreens.Length;
        payload["workingArea"] = Screen.FromControl(form).WorkingArea.ToString();
        payload["dpiMode"] = DpiAwareness.ActualMode;
        payload["dpiLastError"] = DpiAwareness.LastError;
        // 主题状态与调色板：深色模式是不是真的生效、每个控件到底拿了什么颜色，
        // 光看控件树看不出来（位置可以完全一样而颜色不对）。
        payload["dark"] = ModernTheme.IsDark;
        // 窗体**自己**的颜色也要查 —— 它不在 controls 列表里，
        // 而「启动器面板没跟着变深」正是这一处（2026-09-27 抓到）。
        payload["formBack"] = Hex(form.BackColor);
        payload["formFore"] = Hex(form.ForeColor);
        payload["palette"] = new Dictionary<string, object>
        {
            { "surface", Hex(ModernTheme.Surface) }, { "card", Hex(ModernTheme.Card) },
            { "border", Hex(ModernTheme.Border) }, { "text", Hex(ModernTheme.Text) },
            { "textSecondary", Hex(ModernTheme.TextSecondary) }, { "accent", Hex(ModernTheme.Accent) },
            { "onAccent", Hex(ModernTheme.OnAccent) }, { "selection", Hex(ModernTheme.Selection) }
        };
        payload["controls"] = rows;
        File.WriteAllText(path, new JavaScriptSerializer().Serialize(payload), new UTF8Encoding(false));
    }

    /// 颜色 → #RRGGBB（带 alpha 时 #AARRGGBB），给 --ui-probe 用。
    private static string Hex(Color color)
    {
        return color.A == 255
            ? "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2")
            : "#" + color.A.ToString("X2") + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
    }

    private static void CollectControls(Control root, Control container, List<Dictionary<string, object>> rows, int depth)
    {
        foreach (Control child in container.Controls)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            row["type"] = child.GetType().Name;
            row["name"] = child.Name;
            row["parentName"] = container.Name;
            row["parent"] = ReferenceEquals(container, root)
                ? "(form)"
                : container.GetType().Name + ":" + (container.Text == null ? "" : container.Text);
            row["text"] = child.Text == null ? "" : child.Text;
            row["x"] = child.Left;
            row["y"] = child.Top;
            row["w"] = child.Width;
            row["h"] = child.Height;
            row["visible"] = child.Visible;
            row["enabled"] = child.Enabled;
            row["depth"] = depth;
            row["fontFamily"] = child.Font.FontFamily.Name;
            if (child is TextBox || child is ComboBox) row["nativeFontPixels"] = NativeFontPixels(child);
            row["fontPixels"] = child.Font.Unit == GraphicsUnit.Pixel ? child.Font.Size : child.Font.SizeInPoints * DpiLayout.For(root) * 96f / 72f;
            // 实际生效的颜色（含内层控件，比如 ModernInput 里那个 TextBox）——
            // 深色模式下「哪一块还是浅底」全靠这两个字段定位。
            row["back"] = Hex(child.BackColor);
            row["fore"] = Hex(child.ForeColor);
            ListView list = child as ListView;
            if (list != null)
            {
                row["columns"] = list.Columns.Count;
                var widths = new List<int>(); foreach (ColumnHeader column in list.Columns) widths.Add(column.Width);
                row["columnWidths"] = widths; row["clientWidth"] = list.ClientSize.Width;
            }
            ComboBox combo = child as ComboBox;
            if (combo != null) row["items"] = combo.Items.Count;
            rows.Add(row);
            if (child.HasChildren) CollectControls(root, child, rows, depth + 1);
        }
    }

}
