using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

internal static partial class LiveHub
{
    // Bridges the existing light/dark palette to AntdUI's global configuration.
    // Only initialises when the first workbench control is built so console-only
    // commands (--plan/--self-test) never load or touch UI state.
    internal static class AntdUiBridge
    {
        private static bool ready;

        internal static void Initialize()
        {
            if (ready) return;
            ready = true;
            try
            {
                AntdUI.Config.IsDark = ModernTheme.IsDark;
                AntdUI.Style.SetPrimary(ModernTheme.Accent);
                ApplyPalette();
                ModernTheme.Changed += ApplyPalette;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AntdUI init failed: " + ex);
            }
        }

        private static void ApplyPalette()
        {
            try
            {
                AntdUI.Config.IsDark = ModernTheme.IsDark;
                AntdUI.Style.SetPrimary(ModernTheme.Accent);
                if (ModernTheme.IsDark) AntdUI.Config.Theme().Dark(ModernTheme.Surface, ModernTheme.Text);
                else AntdUI.Config.Theme().Light(ModernTheme.Surface, ModernTheme.Text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AntdUI palette failed: " + ex);
            }
        }
    }
}
