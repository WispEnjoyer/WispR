using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>The floating box with tray icons, keyboard layout, network, volume, battery, clock and notifications.</summary>
    sealed class SystemBoxForm : BarForm
    {
        readonly TrayService tray; // may be null when tray icons are turned off
        readonly Timer clock = new Timer { Interval = 1000 };
        Font glyphFont, smallGlyphFont, textFont, smallTextFont;
        int barHeight;

        // cached readings
        string kbd, kbdFull;
        SystemStatus.Net net;
        double upSpeed, downSpeed;
        float volume; bool muted, hasVolume;
        bool hasBattery, charging; int battery;
        string lastStatusSig;
        Size lastSize;

        public Action OpenSettings;
        public Action HideBars;

        public SystemBoxForm(Settings settings, Backdrop backdrop, TrayService tray) : base(settings, backdrop)
        {
            this.tray = tray;
            Text = "WispR System";
            if (tray != null) tray.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Relayout); };
            clock.Tick += (o, e) => OnTick();
            clock.Start();
            _ = Handle;
            StartPolling();
        }

        public void SetHeight(int h) { barHeight = h; }

        public override void ApplyTheme()
        {
            glyphFont?.Dispose(); smallGlyphFont?.Dispose(); textFont?.Dispose(); smallTextFont?.Dispose();
            glyphFont = new Font(GlyphFamily, 11.5f);
            smallGlyphFont = new Font(GlyphFamily, 8f);
            textFont = new Font("Segoe UI", 9f);
            smallTextFont = new Font("Segoe UI", 8f);
            base.ApplyTheme();
            Relayout();
        }

        void OnTick()
        {
            CheckLayout(); // the clock
            Invalidate();
        }

        // ---------- status readings (on a background thread, so clicks never wait for them) ----------

        sealed class Reading
        {
            public string Kbd, KbdFull;
            public bool HasVolume, Muted, HasBattery, Charging, Full;
            public float Volume;
            public double Up, Down;
            public SystemStatus.Net Net;
            public int Battery;
        }

        System.Threading.Thread poller;
        volatile bool stopPolling;

        void StartPolling()
        {
            poller = new System.Threading.Thread(() =>
            {
                int n = 0;
                while (!stopPolling)
                {
                    try
                    {
                        var r = new Reading { Full = n % 5 == 0 }; // network & battery every 5 s, the rest every second
                        if (settings.ShowKeyboardLayout) r.Kbd = SystemStatus.GetKeyboardLayoutName(out r.KbdFull);
                        if (settings.ShowVolume) r.HasVolume = SystemStatus.TryGetVolume(out r.Volume, out r.Muted);
                        if (settings.ShowNetSpeed) SystemStatus.SampleSpeed(out r.Up, out r.Down);
                        if (r.Full)
                        {
                            if (settings.ShowNetwork) r.Net = SystemStatus.GetNetwork();
                            if (settings.ShowBattery) r.HasBattery = SystemStatus.TryGetBattery(out r.Battery, out r.Charging);
                        }
                        if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => Apply(r)));
                    }
                    catch { /* keep polling */ }
                    n++;
                    System.Threading.Thread.Sleep(1000);
                }
            })
            { IsBackground = true, Name = "SystemStatus" };
            poller.Start();
        }

        void Apply(Reading r)
        {
            kbd = r.Kbd; kbdFull = r.KbdFull;
            hasVolume = r.HasVolume; volume = r.Volume; muted = r.Muted;
            upSpeed = r.Up; downSpeed = r.Down;
            if (r.Full) { net = r.Net; hasBattery = r.HasBattery; battery = r.Battery; charging = r.Charging; }
            CheckLayout();
            Invalidate();
        }

        /// <summary>Instant volume feedback after scrolling / muting.</summary>
        void ReadVolumeNow()
        {
            if (settings.ShowVolume) hasVolume = SystemStatus.TryGetVolume(out volume, out muted);
        }

        void CheckLayout()
        {
            // Re-measure only when something that changes the width appeared/disappeared.
            string sig = (kbd != null) + "|" + hasVolume + "|" + hasBattery + "|" + DateTime.Now.ToString(TimeFormat()) + "|" + SpeedWidth() + "|" + kbd;
            if (sig != lastStatusSig) { lastStatusSig = sig; Relayout(); }
        }

        void Relayout()
        {
            var sz = Measure();
            if (sz != lastSize) { lastSize = sz; RequestRelayout(); }
            Invalidate();
        }

        string TimeFormat()
        {
            var f = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
            if (settings.ShowSeconds && !f.Contains("s")) f = f.Replace("mm", "mm:ss");
            return f;
        }

        // ---------- layout ----------

        public override Size Measure()
        {
            items.Clear();
            int h = barHeight > 0 ? barHeight : S(48);
            if (textFont == null) return new Size(h, h); // fonts arrive with the first ApplyTheme
            int pad = S(4), x = pad, cell = h - S(8), top = (h - cell) / 2, narrow = S(28);

            BarItem Add(int width, Func<string> tip, Action<Graphics, Rectangle, bool> paint, Action<MouseButtons, Point, bool> click, Action<int> wheel = null)
            {
                var it = new BarItem { Bounds = new Rectangle(x, top, width, cell), Tooltip = tip, Paint = paint, Click = click, Wheel = wheel };
                items.Add(it);
                x += width + S(1);
                return it;
            }

            // tray icons
            if (settings.ShowTray && tray != null)
            {
                var visible = tray.Icons.Where(i => !i.Hidden && i.Icon != null).ToList();
                if (visible.Count > 0)
                {
                    Add(S(22), () => settings.TrayExpanded ? "Hide tray icons" : "Show tray icons",
                        (g, r, hv) => DrawGlyph(g, settings.TrayExpanded ? (AtBottom() ? "" : "") : "", smallGlyphFont, r, T.SubText),
                        (b, p, d) => { if (b == MouseButtons.Left) { settings.TrayExpanded = !settings.TrayExpanded; settings.Save(); Relayout(); } });
                    if (settings.TrayExpanded)
                        foreach (var icon in visible)
                        {
                            var ic = icon;
                            Add(narrow, () => ic.Tip,
                                (g, r, hv) =>
                                {
                                    int px = S(16);
                                    if (ic.Icon != null) g.DrawImage(ic.Icon, new Rectangle(r.X + (r.Width - px) / 2, r.Y + (r.Height - px) / 2, px, px));
                                },
                                (b, p, d) =>
                                {
                                    // WispR's own icon: its menu directly in our style
                                    if (b == MouseButtons.Right && Native.GetPid(ic.Hwnd) == ownPid && TrayContext.Instance != null)
                                    {
                                        var own = NewMenu();
                                        TrayContext.Instance.FillBarMenu(own);
                                        PopUp(own, p);
                                        return;
                                    }
                                    // right-click: the app's menu is shown in our style (if it's a standard menu)
                                    if (b == MouseButtons.Right && settings.ThemedTrayMenus) TrayMenusFor().Expect(ic, p);
                                    tray.Click(ic, b, d, p);
                                });
                        }
                    x += S(4);
                }
            }

            if (settings.ShowKeyboardLayout && kbd != null)
                Add(Measure(kbd) + S(14), () => kbdFull + "\nClick to switch (Win+Space)",
                    (g, r, hv) => TextRenderer.DrawText(g, kbd, smallTextFont, r, T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter),
                    (b, p, d) => { if (b == MouseButtons.Left) Native.SendCombo(Native.VK_LWIN, Native.VK_SPACE); else if (b == MouseButtons.Right) SettingsMenu(p, "Keyboard settings", "ms-settings:keyboard"); });

            if (settings.ShowNetSpeed)
            {
                Add(SpeedWidth(), () => "Upload " + SystemStatus.FormatSpeed(upSpeed) + "\nDownload " + SystemStatus.FormatSpeed(downSpeed) + "\nClick for Task Manager",
                    (g, r, hv) =>
                    {
                        string up = "\u2191 " + SystemStatus.FormatSpeed(upSpeed), down = "\u2193 " + SystemStatus.FormatSpeed(downSpeed);
                        int lh = smallTextFont.Height, y = r.Y + (r.Height - lh * 2) / 2;
                        int block = Math.Max(Measure(up), Measure(down));
                        int x = r.X + (r.Width - block) / 2; // both lines share a left edge so the arrows line up
                        var flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                        TextRenderer.DrawText(g, up, smallTextFont, new Rectangle(x, y, block + S(2), lh), SpeedColor(upload: true, upSpeed), flags);
                        TextRenderer.DrawText(g, down, smallTextFont, new Rectangle(x, y + lh, block + S(2), lh), SpeedColor(upload: false, downSpeed), flags);
                    },
                    (b, p, d) => { if (b == MouseButtons.Left) Launcher.Start("taskmgr.exe", null, null); else if (b == MouseButtons.Right) SettingsMenu(p, "Network settings", "ms-settings:network"); });
            }

            if (settings.ShowNetwork)
                Add(narrow, () => net == SystemStatus.Net.Wifi ? "Wi-Fi connected" : net == SystemStatus.Net.Ethernet ? "Ethernet connected" : "No internet connection",
                    (g, r, hv) => DrawGlyph(g, net == SystemStatus.Net.Wifi ? "" : net == SystemStatus.Net.Ethernet ? "" : "", glyphFont, r, T.Text),
                    (b, p, d) => { if (b == MouseButtons.Left) QuickSettings(); else if (b == MouseButtons.Right) SettingsMenu(p, "Network settings", "ms-settings:network"); });

            if (settings.ShowVolume && hasVolume)
            {
                BarItem volItem = null;
                volItem = Add(narrow, () => muted ? "Muted" : "Volume " + (int)Math.Round(volume * 100) + "%\nClick for the slider · scroll to change · middle-click to mute",
                    (g, r, hv) => DrawGlyph(g, VolumeGlyph(), glyphFont, r, T.Text),
                    (b, p, d) =>
                    {
                        if (b == MouseButtons.Left) OpenVolumePopup(RectangleToScreen(volItem.Bounds));
                        else if (b == MouseButtons.Middle) { SystemStatus.ToggleMute(); ReadVolumeNow(); Invalidate(); }
                        else if (b == MouseButtons.Right) SettingsMenu(p, "Sound settings", "ms-settings:sound");
                    },
                    delta =>
                    {
                        SystemStatus.SetVolume(volume + Math.Sign(delta) * 0.02f);
                        ReadVolumeNow();
                        Invalidate();
                    });
            }

            if (settings.ShowBattery && hasBattery)
                Add(narrow, () => "Battery " + battery + "%" + (charging ? " · plugged in" : ""),
                    (g, r, hv) => DrawGlyph(g, BatteryGlyph(), glyphFont, r, T.Text),
                    (b, p, d) => { if (b == MouseButtons.Left) QuickSettings(); else if (b == MouseButtons.Right) SettingsMenu(p, "Power & battery settings", "ms-settings:powersleep"); });

            if (settings.ShowClock)
            {
                string time = DateTime.Now.ToString(TimeFormat());
                string date = DateTime.Now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern);
                int w = Math.Max(TextRenderer.MeasureText(time, textFont).Width, settings.ShowDate ? TextRenderer.MeasureText(date, smallTextFont).Width : 0) + S(14);
                BarItem clockItem = null;
                clockItem = Add(w, () => DateTime.Now.ToString("D") + "\nClick for the calendar",
                    (g, r, hv) =>
                    {
                        string t = DateTime.Now.ToString(TimeFormat());
                        if (settings.ShowDate)
                        {
                            int th = textFont.Height, dh = smallTextFont.Height, y = r.Y + (r.Height - th - dh) / 2;
                            TextRenderer.DrawText(g, t, textFont, new Rectangle(r.X, y, r.Width, th), T.Text, TextFormatFlags.HorizontalCenter);
                            TextRenderer.DrawText(g, DateTime.Now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern),
                                smallTextFont, new Rectangle(r.X, y + th, r.Width, dh), T.SubText, TextFormatFlags.HorizontalCenter);
                        }
                        else TextRenderer.DrawText(g, t, textFont, r, T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    },
                    (b, p, d) => { if (b == MouseButtons.Left) OpenCalendar(RectangleToScreen(clockItem.Bounds)); else if (b == MouseButtons.Right) SettingsMenu(p, "Date & time settings", "ms-settings:dateandtime"); });
            }

            if (settings.ShowNotifications)
                Add(narrow, () => "Notifications",
                    (g, r, hv) => DrawGlyph(g, "", glyphFont, r, T.Text),
                    (b, p, d) => { if (b == MouseButtons.Left) Notifications(); else if (b == MouseButtons.Right) SettingsMenu(p, "Notification settings", "ms-settings:notifications"); });

            if (settings.ShowDesktopCorner)
            {
                var it = Add(S(10), () => "Show desktop",
                    (g, r, hv) =>
                    {
                        using var pen = new Pen(hv ? T.Accent : T.Border, Math.Max(1, S(1.5f)));
                        int cx = r.X + r.Width / 2;
                        g.DrawLine(pen, cx, r.Y + r.Height / 4, cx, r.Bottom - r.Height / 4);
                    },
                    (b, p, d) => { if (b == MouseButtons.Left) Native.WinPlus('D'); });
                it.HoverBackground = false;
            }

            return new Size(Math.Max(x - S(1) + pad, h), h);
        }

        int Measure(string text) =>
            TextRenderer.MeasureText(text, smallTextFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

        /// <summary>A steady width that fits every value it can show, so the box never resizes as numbers change.</summary>
        int SpeedWidth()
        {
            if (smallTextFont == null) return 0;
            int text = new[] { "\u2191 888 KB/s", "\u2191 88.8 MB/s", "\u2191 8.88 GB/s" }.Max(Measure);
            return text + S(8);
        }

        /// <summary>Upload orange, download green; dimmer while (almost) idle.</summary>
        Color SpeedColor(bool upload, double bytesPerSec)
        {
            Color c = T.IsLight
                ? (upload ? Color.FromArgb(234, 88, 12) : Color.FromArgb(22, 163, 74))
                : (upload ? Color.FromArgb(251, 146, 60) : Color.FromArgb(74, 222, 128));
            if (bytesPerSec >= 1024) return c;
            var d = T.SubText;
            return Color.FromArgb((c.R + d.R) / 2, (c.G + d.G) / 2, (c.B + d.B) / 2);
        }

        bool AtBottom() => settings.TaskbarEdge != "Top";

        string VolumeGlyph()
        {
            if (muted || volume <= 0.001f) return "";
            return volume < 0.34f ? "" : volume < 0.67f ? "" : "";
        }

        string BatteryGlyph()
        {
            int level = Math.Max(0, Math.Min(10, (int)Math.Round(battery / 10.0)));
            if (charging) return level >= 10 ? "" : ((char)(0xE85A + level)).ToString();
            return level >= 10 ? "" : ((char)(0xE850 + level)).ToString();
        }

        CalendarPopup calendarPopup;

        void OpenCalendar(Rectangle item)
        {
            calendarPopup ??= new CalendarPopup(settings);
            calendarPopup.ToggleAt(item, AtBottom(), Bounds.Right);
        }

        VolumePopup volumePopup;

        void OpenVolumePopup(Rectangle item)
        {
            if (volumePopup == null)
            {
                volumePopup = new VolumePopup(settings);
                volumePopup.VolumeChanged += () => { ReadVolumeNow(); Invalidate(); };
            }
            volumePopup.ToggleAt(item, AtBottom(), Bounds.Right);
        }

        // Windows' own flyouts still work with its taskbar hidden.
        static void QuickSettings() => Native.WinPlus('A');
        static void Notifications() => Native.WinPlus(SystemStatus.IsWindows11 ? 'N' : 'A');

        void SettingsMenu(Point pt, string settingsLabel, string settingsUri)
        {
            var m = NewMenu();
            AddItem(m, settingsLabel, () => Launcher.Start(settingsUri, null, null));
            m.AddSeparator();
            AddGeneral(m);
            ShowMenu(m, pt);
        }

        protected override void OnBackgroundRightClick(Point screen)
        {
            var m = NewMenu();
            AddGeneral(m);
            ShowMenu(m, screen);
        }

        void AddGeneral(BarMenu m)
        {
            AddItem(m, "WispR settings…", () => OpenSettings?.Invoke());
            AddItem(m, "Hide bars (double-tap Windows key)", () => HideBars?.Invoke());
        }

        void ShowMenu(BarMenu m, Point pt) => PopUp(m, pt);

        TrayMenus trayMenus;
        static readonly uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        TrayMenus TrayMenusFor() => trayMenus ??= new TrayMenus(tray, () => NewMenu(), (m, pt) => PopUp(m, pt));

        protected override void Dispose(bool disposing)
        {
            if (disposing) trayMenus?.Dispose();
            if (disposing) { clock.Dispose(); stopPolling = true; volumePopup?.Dispose(); calendarPopup?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
