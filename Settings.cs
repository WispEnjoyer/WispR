using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WispR
{
    sealed class Theme
    {
        public string Name;
        public Color Background, Surface, Selection, Text, SubText, Accent, Border;

        public Theme Clone() => (Theme)MemberwiseClone();
        public bool IsLight => Background.GetBrightness() > 0.5f;

        static Color C(string hex) => ColorTranslator.FromHtml(hex);

        static Theme Make(string name, string bg, string surface, string sel, string text, string sub, string accent, string border) =>
            new Theme { Name = name, Background = C(bg), Surface = C(surface), Selection = C(sel), Text = C(text), SubText = C(sub), Accent = C(accent), Border = C(border) };

        public static readonly Theme[] Presets =
        {
            Make("Midnight",        "#1E1E20", "#2A2A2E", "#34353C", "#F0F0F0", "#96969E", "#60A5FA", "#3C3C42"),
            Make("Light",           "#F7F7F8", "#FFFFFF", "#E3E8F0", "#1E1E22", "#6B6B75", "#2563EB", "#D6D6DC"),
            Make("Nord",            "#2E3440", "#3B4252", "#434C5E", "#ECEFF4", "#9AA3B5", "#88C0D0", "#4C566A"),
            Make("Dracula",         "#282A36", "#343746", "#44475A", "#F8F8F2", "#8F99C2", "#BD93F9", "#44475A"),
            Make("Catppuccin",      "#1E1E2E", "#313244", "#45475A", "#CDD6F4", "#9399B2", "#CBA6F7", "#45475A"),
            Make("Gruvbox",         "#282828", "#3C3836", "#504945", "#EBDBB2", "#A89984", "#FABD2F", "#504945"),
            Make("Rosé Pine",       "#191724", "#1F1D2E", "#2A2740", "#E0DEF4", "#908CAA", "#EBBCBA", "#403D52"),
            Make("Solarized Light", "#FDF6E3", "#EEE8D5", "#E6DFC8", "#073642", "#839496", "#268BD2", "#DDD6C1"),
        };

        public static readonly (string Key, string Label)[] ColorSlots =
        {
            ("background", "Background"), ("surface", "Search box"), ("selection", "Selection"), ("accent", "Accent"),
            ("text", "Text"), ("subtext", "Secondary text"), ("border", "Border"),
        };

        public Color Get(string key) => key switch
        {
            "background" => Background, "surface" => Surface, "selection" => Selection, "text" => Text,
            "subtext" => SubText, "accent" => Accent, "border" => Border, _ => Color.Magenta,
        };

        public void Set(string key, Color c)
        {
            switch (key)
            {
                case "background": Background = c; break;
                case "surface": Surface = c; break;
                case "selection": Selection = c; break;
                case "text": Text = c; break;
                case "subtext": SubText = c; break;
                case "accent": Accent = c; break;
                case "border": Border = c; break;
            }
        }
    }

    /// <summary>All user settings, stored in %APPDATA%\WispR\settings.ini.</summary>
    sealed class Settings
    {
        // ----- appearance -----
        public Theme Theme = Theme.Presets[0].Clone();
        public string BackgroundImage = "";
        public bool UseWallpaper = false;       // use the desktop wallpaper instead of BackgroundImage
        public bool MatchImageColors = false;   // derive the theme from the image
        public bool ShowImage = true;           // draw the picture behind the launcher/bars (off = only use its colours)
        public string PaletteMode = "Auto";     // Auto / Dark / Light for image-derived themes
        public int ImageDim = 55;               // 0-90 %: how strongly the background colour covers the image
        public bool ImageBlur = true;
        public int Opacity = 100;               // 60-100 %
        public int Width = 640;                 // launcher width, logical pixels

        // ----- general -----
        public bool InterceptWinKey = true;
        public string SearchEngine = "Google";
        public string LauncherPosition = "Bottom"; // Bottom (above the taskbar) / Center / Top
        public string WallpaperSource = "Favorites"; // picker shows: Favorites (Wallpaper Engine ♥) / Engine (all of Wallpaper Engine) / All (+ pictures)
        public string WallpaperFolder = "";     // wallpapers shown when you type "wallpaper" ("" = automatic)
        public string AccountName = "Wisp";     // name shown in the Start menu (never the real account name)
        public bool ShowAccountPicture = true;  // your Windows account picture in the Start menu
        public int DoubleTapMs = 350;           // Windows key twice within this = hide/show bars

        // ----- taskbar -----
        public bool TaskbarEnabled = true;
        public bool TaskbarAllMonitors = true;      // a taskbar on every monitor
        public bool TaskbarAppsOnOwnMonitor = true; // each taskbar shows the windows on its own monitor
        public string TaskbarEdge = "Bottom";   // Bottom / Top
        public string TaskbarAlign = "Center";  // Center / Left
        public string SystemBoxPlace = "Right"; // Right / Left / Beside
        public string TaskbarIconSize = "Medium"; // Small / Medium / Large
        public int EdgeGap = 8;                 // floating distance from the screen edge
        public bool ReserveSpace = true;        // keep maximized windows clear of the bar
        public bool ScreenFrame = true;         // a frame around the whole screen that the bars are part of
        public int FrameThickness = 8;          // its width on the three sides without bars
        public bool FillWhenMaximized = true;   // solid full-width band behind the bars while a window is maximized
        public bool ShowStartButton = true;
        public bool ThemedTrayMenus = true;     // show tray icons' right-click menus in WispR's style
        public bool TopPanel = true;
        public bool ShowNotch = true;          // the little tab at the top edge that marks the drop-down
        public int TopPanelDelay = 90;         // ms the mouse rests at the top edge before the drop-down opens
        public string TopPanelTab = "Auto";    // which tab it opens on: Auto (Media while something plays) / Media / Performance / Last
        public string VinylStyle = "Random";   // the record's look: Random (picked per song) or one style for all
        public int CornerRadius = 16;          // roundness of the launcher, popups, drop-down, timers and frame (0-28)
        public string Animations = "Normal";   // Off / Fast / Normal / Relaxed
        public int LauncherRows = 8;           // results shown at once
        public bool TimerSound = true;         // a soft chime when a timer ends
        public int VinylRpm = 12;               // how fast the record in the Media tab turns (0 = still)            // media + performance in a drop-down from the top edge (instead of the boxes)
        public bool ShowTaskView = false;
        public bool ShowTray = true;
        public bool TrayExpanded = false;
        public bool ShowKeyboardLayout = true;
        public bool ShowNetwork = true;
        public bool ShowNetSpeed = true;
        public bool ShowVolume = true;
        public bool ShowBattery = true;
        public bool ShowClock = true;
        public bool ShowDate = true;
        public bool ShowSeconds = false;
        public bool ShowNotifications = true;
        public bool ShowDesktopCorner = true;
        public int ExplorerAutoHideOriginal = -1; // Windows taskbar state to restore; -1 = nothing to restore

        public List<string> Pinned = new List<string>();        // pinned inside the launcher
        public List<string> WallpaperHearts = new List<string>(); // wallpapers ♥'d in WispR's picker (project.json paths)
        public List<string> TaskbarPinned = new List<string>(); // pinned on the taskbar (app keys or "exe:<path>")

        public event Action Changed;
        /// <summary>Raised when the theme was regenerated from an image (so the settings window can refresh).</summary>
        public event Action ThemeRecomputed;
        public void RaiseThemeRecomputed() => ThemeRecomputed?.Invoke();

        public static readonly string[] PaletteModes = { "Auto", "Dark", "Light" };

        public static readonly Dictionary<string, string> SearchEngines = new Dictionary<string, string>
        {
            ["Google"] = "https://www.google.com/search?q=",
            ["Bing"] = "https://www.bing.com/search?q=",
            ["DuckDuckGo"] = "https://duckduckgo.com/?q=",
            ["Ecosia"] = "https://www.ecosia.org/search?q=",
            ["Brave"] = "https://search.brave.com/search?q=",
        };

        public string SearchUrl(string text) =>
            (SearchEngines.TryGetValue(SearchEngine, out var u) ? u : SearchEngines["Google"]) +
            Uri.EscapeDataString(text.Length > 2000 ? text.Substring(0, 2000) : text); // very long text would throw

        public bool IsPinned(AppEntry e) => Pinned.Contains(e.Key);

        public void TogglePin(AppEntry e)
        {
            if (!Pinned.Remove(e.Key)) Pinned.Add(e.Key);
            NotifyChanged();
        }

        public void NotifyChanged()
        {
            Validate();
            ApplyGlobals();
            Save();
            Changed?.Invoke();
        }

        /// <summary>Settings that everything reads directly: corner roundness, animation speed, the record's look.</summary>
        public void ApplyGlobals()
        {
            Ui.Corner = CornerRadius;
            Anim.Speed = Animations == "Off" ? 0 : Animations == "Fast" ? 0.6 : Animations == "Relaxed" ? 1.45 : 1;
            Vinyl.Fixed = VinylStyle != "Random" && Enum.TryParse(VinylStyle, out Vinyl.Style st) ? st : (Vinyl.Style?)null;
        }

        public void ResetAppearance()
        {
            var d = new Settings();
            Theme = d.Theme; BackgroundImage = d.BackgroundImage; ImageDim = d.ImageDim;
            UseWallpaper = d.UseWallpaper; MatchImageColors = d.MatchImageColors; PaletteMode = d.PaletteMode;
            ImageBlur = d.ImageBlur; ShowImage = d.ShowImage; Opacity = d.Opacity; Width = d.Width;
        }

        // ---------- persistence: one "Name=value" line per simple field ----------

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WispR", "settings.ini");

        static readonly FieldInfo[] SimpleFields = typeof(Settings)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(bool) || f.FieldType == typeof(int) || f.FieldType == typeof(string))
            .ToArray();

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                string preset = null;
                var colors = new Dictionary<string, Color>();
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "theme") preset = v;
                    else if (k == "image") s.BackgroundImage = v; // older files
                    else if (k == "pin") { if (v.Length > 0) s.Pinned.Add(v); }
                    else if (k == "tbpin") { if (v.Length > 0) s.TaskbarPinned.Add(v); }
                    else if (k == "wpfav") { if (v.Length > 0) s.WallpaperHearts.Add(v); }
                    else if (k.StartsWith("color.")) { try { colors[k.Substring(6)] = ColorTranslator.FromHtml(v); } catch { } }
                    else
                    {
                        var f = SimpleFields.FirstOrDefault(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase));
                        if (f == null) continue;
                        if (f.FieldType == typeof(bool)) f.SetValue(s, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                        else if (f.FieldType == typeof(int)) { if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) f.SetValue(s, n); }
                        else f.SetValue(s, v);
                    }
                }
                var basis = Theme.Presets.FirstOrDefault(p => p.Name == preset) ?? Theme.Presets[0];
                s.Theme = basis.Clone();
                s.Theme.Name = preset ?? basis.Name;
                foreach (var kv in colors) s.Theme.Set(kv.Key, kv.Value);
            }
            catch { /* use defaults */ }
            s.Validate();
            s.ApplyGlobals();
            s.TrayExpanded = false; // tray icons always start tucked away; the arrow shows them
            return s;
        }

        void Validate()
        {
            ImageDim = Clamp(ImageDim, 0, 90);
            Opacity = Clamp(Opacity, 60, 100);
            Width = Clamp(Width, 480, 1000);
            EdgeGap = Clamp(EdgeGap, 0, 40);
            FrameThickness = Clamp(FrameThickness, 2, 40);
            VinylRpm = Clamp(VinylRpm, 0, 45);
            DoubleTapMs = Clamp(DoubleTapMs, 150, 800);
            TopPanelDelay = Clamp(TopPanelDelay, 0, 600);
            CornerRadius = Clamp(CornerRadius, 0, 28);
            LauncherRows = Clamp(LauncherRows, 4, 12);
            if (TopPanelTab != "Media" && TopPanelTab != "Performance" && TopPanelTab != "Last") TopPanelTab = "Auto";
            if (Animations != "Off" && Animations != "Fast" && Animations != "Relaxed") Animations = "Normal";
            if (VinylStyle != "Random" && !Enum.GetNames(typeof(Vinyl.Style)).Contains(VinylStyle)) VinylStyle = "Random";
            if (!SearchEngines.ContainsKey(SearchEngine)) SearchEngine = "Google";
            if (LauncherPosition != "Center" && LauncherPosition != "Top") LauncherPosition = "Bottom";
            if (WallpaperSource != "Engine" && WallpaperSource != "All") WallpaperSource = "Favorites";
            if (!PaletteModes.Contains(PaletteMode)) PaletteMode = "Auto";
            if (TaskbarEdge != "Top") TaskbarEdge = "Bottom";
            if (TaskbarAlign != "Left") TaskbarAlign = "Center";
            if (SystemBoxPlace != "Left" && SystemBoxPlace != "Beside") SystemBoxPlace = "Right";
            if (TaskbarIconSize != "Small" && TaskbarIconSize != "Large") TaskbarIconSize = "Medium";
        }

        static string OneLine(string v) => (v ?? "").Replace("\r", " ").Replace("\n", " ");

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                sb.AppendLine("theme=" + Theme.Name);
                foreach (var (key, _) in Theme.ColorSlots)
                    sb.AppendLine("color." + key + "=" + ColorTranslator.ToHtml(Theme.Get(key)));
                foreach (var f in SimpleFields)
                {
                    object v = f.GetValue(this);
                    string text = v is bool b ? (b ? "1" : "0") : v is int i ? i.ToString(CultureInfo.InvariantCulture) : (string)v ?? "";
                    sb.AppendLine(f.Name + "=" + text.Replace("\r", " ").Replace("\n", " "));
                }
                foreach (var p in Pinned) sb.AppendLine("pin=" + OneLine(p));
                foreach (var p in TaskbarPinned) sb.AppendLine("tbpin=" + OneLine(p));
                foreach (var p in WallpaperHearts) sb.AppendLine("wpfav=" + OneLine(p));
                // Write to a temporary file first and swap it in: a crash or power cut mid-save can't leave
                // an empty settings file (which would also lose how to restore the Windows taskbar).
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                try
                {
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", true);
                    else File.Move(tmp, FilePath);
                }
                catch // e.g. the file is locked by a backup tool: fall back to a plain write
                {
                    File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch { /* not fatal */ }
        }

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "WispR";

        public static bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) != null;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValue, false);
                key.DeleteValue(OldRunValue, false);
            }
        }

        const string OldRunValue = "QuickStart";

        /// <summary>The app used to be called QuickStart: carry its autostart entry over to the new name.</summary>
        public static void MigrateOldName()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key?.GetValue(OldRunValue) == null) return;
                key.DeleteValue(OldRunValue, false);
                key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
            }
            catch { }
        }
    }

    static class Rename
    {
        /// <summary>Moves %APPDATA%\QuickStart (settings, usage, logs, hearts) to %APPDATA%\WispR once.</summary>
        public static void MigrateDataFolder()
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string oldDir = System.IO.Path.Combine(root, "QuickStart"), newDir = System.IO.Path.Combine(root, "WispR");
                if (!System.IO.Directory.Exists(oldDir) || System.IO.Directory.Exists(newDir)) return;
                try { System.IO.Directory.Move(oldDir, newDir); return; } catch { }
                CopyDir(oldDir, newDir); // folder in use: copy instead, leave the old one alone
            }
            catch { }
        }

        static void CopyDir(string from, string to)
        {
            System.IO.Directory.CreateDirectory(to);
            foreach (var f in System.IO.Directory.GetFiles(from))
                try { System.IO.File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)), false); } catch { }
            foreach (var d in System.IO.Directory.GetDirectories(from))
                CopyDir(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
        }
    }
}
