using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace WispR
{
    static class Program
    {
        static bool errorBoxOpen;
        static DateTime lastErrorBox = DateTime.MinValue;

        [STAThread]
        static void Main(string[] args)
        {
            Rename.MigrateDataFolder(); // before anything reads settings or opens the log
            Autostart.MigrateOldName();

            // Emergency switch: "WispR.exe --restore-taskbar" brings the Windows taskbar back.
            if (Array.Exists(args, a => a.Equals("--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
            {
                var st = Settings.Load();
                st.TaskbarEnabled = false;
                if (st.ExplorerAutoHideOriginal < 0) st.ExplorerAutoHideOriginal = 0; // assume "always show"
                ExplorerTaskbar.Restore(st);
                st.Save();
                MessageBox.Show("The Windows taskbar is back and the WispR taskbar is turned off.", "WispR");
                return;
            }

            using var mutex = new Mutex(true, "WispR_SingleInstance_7f3a91", out bool isFirst);
            if (!isFirst) return;

            // If anything goes badly wrong, never leave the user without a taskbar.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex) Log.Error("crash", ex);
                TrayContext.Instance?.EmergencyRestore();
            };
            Application.ThreadException += (s, e) =>
            {
                Log.Error("ui", e.Exception);
                // One message at a time, and not again for a few minutes: a fault that repeats (a timer
                // tick) must not stack up dialogs. Everything still goes to the log.
                if (errorBoxOpen || DateTime.Now - lastErrorBox < TimeSpan.FromMinutes(3)) return;
                errorBoxOpen = true;
                try
                {
                    MessageBox.Show("WispR hit an error and kept running:\n" + e.Exception.Message +
                        "\n\nDetails were written to %APPDATA%\\WispR\\log.txt", "WispR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { errorBoxOpen = false; lastErrorBox = DateTime.Now; }
            };
            Log.Write("WispR started (" + Environment.OSVersion + ", " + System.Windows.Forms.Screen.AllScreens.Length + " screen(s))");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }

    /// <summary>Owns the tray icon, keyboard hook, launcher, taskbar and settings window.</summary>
    sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly LauncherForm form;
        readonly KeyboardHook hook;
        readonly AppIndex index;
        readonly Settings settings;
        readonly Backdrop backdrop;
        readonly BarsController bars;
        readonly TimerWidget timers;
        readonly System.Windows.Forms.Timer wallpaperWatch = new System.Windows.Forms.Timer { Interval = 5000 };
        SettingsForm settingsForm;
        bool launcherVisibleBeforeTap;

        public TrayContext()
        {
            settings = Settings.Load();
            ThemeExport.Write(settings.Theme);
            backdrop = new Backdrop(settings);
            backdrop.Refresh();
            var usage = new Usage();
            index = new AppIndex();
            form = new LauncherForm(index, usage, settings, backdrop);
            _ = form.Handle; // create the window handle so BeginInvoke works before first show

            bars = new BarsController(settings, index, backdrop, usage)
            {
                ToggleLauncher = () => form.Toggle(),
                OpenSettings = OpenSettings,
            };
            form.IsOnTaskbar = e => bars.Running && bars.Taskbar.IsPinned(e);
            form.TaskbarAt = p => bars.Running ? bars.BottomTaskbarAt(p) : null;
            form.FrameEdgeAt = p => bars.Running ? bars.FrameEdgeAt(p) : null;
            form.ToggleTaskbarPin = e => { if (bars.Running) bars.Taskbar.TogglePin(e); };
            timers = new TimerWidget(settings) { RightEdgeFor = scr => bars.RightEdgeFor(scr) };

            index.Changed += listChanged =>
            {
                if (!form.IsHandleCreated) return;
                form.BeginInvoke((Action)(() =>
                {
                    form.OnIndexChanged(listChanged);
                    if (listChanged) bars.OnIndexChanged();
                }));
            };
            IconLoader.Size = form.IconPixelSize;
            index.RefreshAsync();

            // Windows key: one tap toggles the launcher, a quick second tap undoes that and toggles the bars.
            hook = new KeyboardHook { Enabled = settings.InterceptWinKey, DoubleTapMs = settings.DoubleTapMs };
            hook.WinTapped += () => form.BeginInvoke((Action)(() =>
            {
                launcherVisibleBeforeTap = form.IsOpen;
                form.Toggle();
            }));
            hook.WinDoubleTapped += () => form.BeginInvoke((Action)(() =>
            {
                if (form.IsOpen != launcherVisibleBeforeTap) form.Toggle();
                bars.ToggleHidden();
            }));
            hook.Start();

            settings.Changed += () => ApplyAll("settings changed");
            // Signing out / shutting down: put the Windows taskbar setting back (WispR hides it again next time).
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => bars.PrepareForSessionEnd();
            form.WallpaperChanged += () => { if (backdrop.Refresh()) ApplyAll("wallpaper changed"); };
            wallpaperWatch.Tick += (s, e) => { if (settings.UseWallpaper && backdrop.Refresh()) ApplyAll("wallpaper file changed"); };
            wallpaperWatch.Start();

            var intercept = new ToolStripMenuItem("Replace Windows key Start menu");
            intercept.Click += (s, e) => { settings.InterceptWinKey = !settings.InterceptWinKey; settings.NotifyChanged(); };
            var taskbarItem = new ToolStripMenuItem("Use WispR taskbar");
            taskbarItem.Click += (s, e) => { settings.TaskbarEnabled = !settings.TaskbarEnabled; settings.NotifyChanged(); };
            var autostart = new ToolStripMenuItem("Start with Windows");
            autostart.Click += (s, e) => Autostart.IsEnabled = !Autostart.IsEnabled;

            var menu = trayMenu = new ContextMenuStrip();
            var open = menu.Items.Add("Open launcher", null, (s, e) => form.ShowLauncher());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add("Settings…", null, (s, e) => OpenSettings());
            menu.Items.Add("Refresh app list", null, (s, e) => index.RefreshAsync());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(intercept);
            menu.Items.Add(taskbarItem);
            menu.Items.Add(autostart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open log file", null, (s, e) =>
            {
                Log.Write("Log opened. " + Log.Resources());
                try { Launcher.Start(Log.FilePath, null, null); } catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            refreshTrayChecks = () =>
            {
                intercept.Checked = settings.InterceptWinKey;
                taskbarItem.Checked = settings.TaskbarEnabled;
                autostart.Checked = Autostart.IsEnabled;
            };
            menu.Opening += (s, e) => refreshTrayChecks();

            tray = new NotifyIcon
            {
                Icon = MakeTrayIcon(),
                Text = "WispR — press Windows to search",
                ContextMenuStrip = menu,
                Visible = true,
            };
            form.Notify = (title, text) => tray.ShowBalloonTip(10000, title, text, ToolTipIcon.Info);
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) form.ShowLauncher(); };

            bars.Apply();
            Instance = this;
        }

        /// <summary>For the crash handler: puts the Windows taskbar back.</summary>
        public static TrayContext Instance;
        ContextMenuStrip trayMenu;
        Action refreshTrayChecks;

        /// <summary>WispR's own tray menu, as a bar menu (so it grows out of the bar like the rest).</summary>
        public void FillBarMenu(BarMenu m)
        {
            refreshTrayChecks?.Invoke();
            foreach (ToolStripItem item in trayMenu.Items)
            {
                if (item is ToolStripSeparator) { m.AddSeparator(); continue; }
                var it = item;
                bool check = it is ToolStripMenuItem mi && mi.Checked;
                m.AddItem((check ? "✓  " : "") + it.Text, () => it.PerformClick(), it.Enabled, bold: it.Font.Bold);
            }
        }
        public void EmergencyRestore()
        {
            try { bars.PrepareForSessionEnd(); } catch { } // the Windows taskbar comes back first, whatever happens next
            try { bars.Dispose(); } catch { }
            try { timers?.Dispose(); } catch { }
        }

        void ApplyAll(string reason)
        {
            Log.Throttled("apply:" + reason, "Refreshing the bars and launcher: " + reason + ".");
            hook.Enabled = settings.InterceptWinKey;
            hook.DoubleTapMs = settings.DoubleTapMs;
            form.ApplySettings();
            bars.Apply();
        }

        void OpenSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(settings);
                settingsForm.PreviewRequested += () => form.ShowLauncher();
                settingsForm.Show();
            }
            settingsForm.Activate();
        }

        protected override void ExitThreadCore()
        {
            wallpaperWatch.Stop();
            bars.Dispose(); // restores the Windows taskbar
            hook.Dispose();
            tray.Visible = false;
            tray.Dispose();
            settingsForm?.Dispose();
            form.Dispose();
            base.ExitThreadCore();
        }

        static Icon MakeTrayIcon()
        {
            // the app's own icon (the glowing mote), at the tray's size
            try
            {
                using var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) return new Icon(exeIcon, SystemInformation.SmallIconSize);
            }
            catch { }
            int sz = SystemInformation.SmallIconSize.Width;
            using var bmp = new Bitmap(sz, sz);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float gap = sz / 8f, cell = (sz - gap * 3) / 2f;
                using var b = new SolidBrush(Color.FromArgb(96, 165, 250));
                for (int r = 0; r < 2; r++)
                    for (int c = 0; c < 2; c++)
                        g.FillRectangle(b, gap + c * (cell + gap), gap + r * (cell + gap), cell, cell);
            }
            IntPtr h = bmp.GetHicon();
            try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
            finally { DestroyIcon(h); } // Icon.FromHandle doesn't own the handle
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }
}
