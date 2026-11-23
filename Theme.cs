using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FocusStudy
{
    /// <summary>
    /// Light and dark colour palettes. Every screen refers to these colours by name (DynamicResource),
    /// so swapping the brushes here recolours the whole app instantly, even in the middle of a session.
    /// </summary>
    internal static class Theme
    {
        public const string Dark = "dark", Light = "light";

        static readonly Dictionary<string, string> DarkColors = new Dictionary<string, string>
        {
            ["Bg"] = "#0F1115", ["Panel"] = "#161A22", ["Panel2"] = "#1E2330", ["Line"] = "#262C3A",
            ["Text"] = "#E6E8EE", ["Muted"] = "#8A93A6", ["Accent"] = "#6C8CFF",
            ["Good"] = "#3DDC97", ["Warn"] = "#FFB547", ["Danger"] = "#FF6B6B",
            ["BtnBg"] = "#232836", ["BtnLine"] = "#2E3445", ["InputBg"] = "#0B0D12",
            ["Hover"] = "#1F2430", ["Selected"] = "#2A3550",
            ["DangerBg"] = "#3A2026", ["DangerLine"] = "#5A2A33",
            ["InfoBg"] = "#141B2E", ["InfoLine"] = "#25335A",
            ["WarnBg"] = "#2A2416", ["WarnLine"] = "#4D3F1E",
        };

        static readonly Dictionary<string, string> LightColors = new Dictionary<string, string>
        {
            ["Bg"] = "#F4F5F8", ["Panel"] = "#FFFFFF", ["Panel2"] = "#E8EBF2", ["Line"] = "#DADEE7",
            ["Text"] = "#1B1F29", ["Muted"] = "#5B6477", ["Accent"] = "#4A6CF7",
            ["Good"] = "#14895A", ["Warn"] = "#A8650A", ["Danger"] = "#D23C3C",
            ["BtnBg"] = "#EDEFF4", ["BtnLine"] = "#D2D7E1", ["InputBg"] = "#FFFFFF",
            ["Hover"] = "#ECEFF5", ["Selected"] = "#DCE4FF",
            ["DangerBg"] = "#FDECEC", ["DangerLine"] = "#F1C2C2",
            ["InfoBg"] = "#EEF2FF", ["InfoLine"] = "#C8D3FA",
            ["WarnBg"] = "#FFF5E0", ["WarnLine"] = "#EFD49C",
        };

        public static string Current { get; private set; } = Dark;
        public static bool IsLight => Current == Light;

        /// <summary>Raised after the palette changes (for things drawn outside WPF, like web pages).</summary>
        public static event Action Changed;

        public static string Normalize(string mode) =>
            string.Equals(mode, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

        public static void Apply(string mode)
        {
            Current = Normalize(mode);
            var colors = IsLight ? LightColors : DarkColors;
            var res = Application.Current.Resources;
            foreach (var kv in colors)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                brush.Freeze();
                res[kv.Key] = brush; // replacing the entry updates every DynamicResource user
            }
            Changed?.Invoke();
        }

        public static string Toggle() { Apply(IsLight ? Dark : Light); return Current; }

        /// <summary>Label for a toggle button: what clicking it switches to.</summary>
        public static string ToggleLabel => IsLight ? "🌙  Dark" : "☀  Light";
    }
}
