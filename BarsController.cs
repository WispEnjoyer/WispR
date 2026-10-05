using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Runs the bars: one taskbar + system box per monitor (tray icons, CPU/RAM and the media box
    /// live on the main monitor only, like Windows), hides the Windows taskbar, reserves screen
    /// space, and hides bars for fullscreen apps or on a double-tap of the Windows key.
    /// </summary>
    sealed class BarsController : IDisposable
    {
        /// <summary>Everything that belongs to one monitor.</summary>
        sealed class MonitorBars
        {
            public string Device;          // Screen.DeviceName
            public bool Primary;
            public TaskbarForm Taskbar;
            public SystemBoxForm SysBox;
            public StripForm Strip;
            public StartBoxForm StartBox;   // Start + power, bottom-left corner
            public FrameForm Frame;         // the border around the whole screen
            public int Corrections;         // how often Windows lost the reserved strip (diagnostics)
            public bool FrameReserved;      // the usable area currently leaves room for the frame
            public AppBarReservation[] SideBars; // the frame's three thin sides, reserved like a taskbar
            public AppBarReservation AppBar;
            public string ReserveKey;
            public bool FullscreenHidden, MaximizedPresent;
            public bool ForcedWorkArea;     // we set the work area directly (fallback)
            public DateTime LastRenew;      // last time the reservation was re-registered
            public int FullscreenTicks;     // consecutive polls that saw a fullscreen app (debounce)
            public DateTime FullscreenSince = DateTime.MinValue; // when the fullscreen app was first seen
            public int MaximizedTicks;      // consecutive polls where "a window is maximized" differed (debounce)

            public Screen Screen => Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Device) ?? Screen.PrimaryScreen;
        }

        readonly Settings settings;
        readonly AppIndex index;
        readonly Backdrop backdrop;
        readonly Usage usage;
        readonly Timer poll = new Timer { Interval = 500 };
        // Window events trigger a check right away; the poll stays as a fallback.
        WinEvents winEvents;
        readonly Timer quickCheck = new Timer { Interval = 25 }; // coalesces bursts of events into one check
        readonly float s;

        WindowTracker tracker;
        TrayService tray;
        PerfBoxForm perfbox;
        MediaBoxForm mediabox;
        StartMenuForm startMenu;
        readonly List<MonitorBars> monitors = new List<MonitorBars>();
        bool running, userHidden;
        string monitorSetup; // which screens / options the monitor list was built for
        int pollCount;

        public Action ToggleLauncher;
        public Action OpenSettings;

        public BarsController(Settings settings, AppIndex index, Backdrop backdrop, Usage usage)
        {
            this.settings = settings;
            this.index = index;
            this.backdrop = backdrop;
            this.usage = usage;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
            poll.Tick += (o, e) => Poll();
            quickCheck.Tick += (o, e) => { quickCheck.Stop(); if (running) CheckScreens(); };
            confirm.Tick += (o, e) => { confirm.Stop(); if (running) CheckScreens(); };
            // Windows may report display changes on another thread: hop back to the UI thread first.
            var ui = System.Threading.SynchronizationContext.Current;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (o, e) =>
            {
                if (ui != null) ui.Post(_ => Later(OnDisplaysChanged), null);
            };

            // Leftover from a crash while the taskbar was on: put Windows' taskbar back.
            if (!settings.TaskbarEnabled && settings.ExplorerAutoHideOriginal >= 0) ExplorerTaskbar.Restore(settings);
        }

        public bool Running => running;
        MonitorBars Main => monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        /// <summary>The main monitor's taskbar (pins are shared by all taskbars).</summary>
        public TaskbarForm Taskbar => Main?.Taskbar;

        /// <summary>Call after any settings change.</summary>
        public void Apply()
        {
            if (settings.TaskbarEnabled && !running) Start();
            else if (!settings.TaskbarEnabled && running) Stop();
            if (!running) return;

            // The tray service only runs while tray icons are shown (main monitor's system box).
            bool trayChanged = settings.ShowTray != (tray != null);
            if (trayChanged)
            {
                tray?.Dispose();
                tray = null;
                if (settings.ShowTray) { tray = new TrayService(); tray.Start(); }
            }
            if (trayChanged || SetupKey() != monitorSetup) BuildMonitors();

            foreach (var m in monitors)
            {
                m.Taskbar.ApplyTheme();
                m.SysBox.SetHeight(m.Taskbar.BarHeight);
                m.SysBox.ApplyTheme();
                m.Strip.ApplyTheme();
                m.StartBox.SetHeight(m.Taskbar.BarHeight);
                m.StartBox.ApplyTheme();
            }
            int h = Main.Taskbar.BarHeight;
            perfbox.SetHeight(h); perfbox.ApplyTheme();
            mediabox.SetHeight(h); mediabox.ApplyTheme();
            topPanel?.SetEnabled(settings.TopPanel);
            LayoutAll();
        }

        void Start()
        {
            running = true;
            tracker = new WindowTracker();
            if (settings.ShowTray) { tray = new TrayService(); tray.Start(); }

            topPanel = new TopPanelForm(settings, backdrop) { TopEdgeFor = TopEdgeFor };
            BarForm.BandTopAt = BandTopAt;
            startMenu = new StartMenuForm(settings, backdrop, index, usage)
            {
                IsOnTaskbar = e => Taskbar != null && Taskbar.IsPinned(e),
                ToggleTaskbarPin = e => Taskbar?.TogglePin(e),
                BeforeLeaving = PrepareForSessionEnd,
            };
            perfbox = new PerfBoxForm(settings, backdrop)
            {
                HideBox = () => Later(() => { settings.ShowPerfBox = false; settings.NotifyChanged(); }),
            };
            perfbox.SizeWanted += LayoutAll;
            mediabox = new MediaBoxForm(settings, backdrop) { ActivateApp = ActivateMediaApp };
            mediabox.SizeWanted += LayoutAll;
            mediabox.HasMediaChanged += LayoutAll;

            BuildMonitors();
            ExplorerTaskbar.Hide(settings);
            tracker.Refresh();
            poll.Start();
            winEvents = new WinEvents();
            winEvents.Changed += h => { if (running && !quickCheck.Enabled) quickCheck.Start(); };
        }

        void Stop()
        {
            running = false;
            poll.Stop();
            quickCheck.Stop();
            confirm.Stop();
            winEvents?.Dispose(); winEvents = null;
            DisposeMonitors();
            startMenu?.Dispose(); startMenu = null;
            topPanel?.Dispose(); topPanel = null;
            perfbox?.Dispose(); perfbox = null;
            mediabox?.Dispose(); mediabox = null;
            tray?.Dispose(); tray = null;
            tracker?.Dispose(); tracker = null;
            ExplorerTaskbar.Restore(settings);
        }

        // ---------- monitors ----------

        string SetupKey() =>
            settings.TaskbarAllMonitors + "|" + settings.TaskbarAppsOnOwnMonitor + "|" +
            string.Join(";", Screen.AllScreens.Select(sc => sc.DeviceName + sc.Bounds + sc.Primary));

        /// <summary>(Re)creates one set of bars per monitor.</summary>
        void BuildMonitors()
        {
            DisposeMonitors();
            monitorSetup = SetupKey();
            var screens = settings.TaskbarAllMonitors ? Screen.AllScreens : new[] { Screen.PrimaryScreen };
            foreach (var sc in screens.OrderByDescending(x => x.Primary))
            {
                var m = new MonitorBars { Device = sc.DeviceName, Primary = sc.Primary };
                string device = sc.DeviceName;
                m.Taskbar = new TaskbarForm(settings, backdrop, index, tracker)
                {
                    ToggleLauncher = () => ToggleLauncher?.Invoke(),
                    OpenSettings = () => OpenSettings?.Invoke(),
                    HideBars = ToggleHidden,
                    // deferred: this is clicked from the taskbar's own menu, which is about to be closed
                    DisableTaskbar = () => Later(() => { settings.TaskbarEnabled = false; settings.NotifyChanged(); }),
                    OpenStartMenu = r => startMenu.Toggle(r, settings.TaskbarEdge != "Top"),
                    // With several monitors each taskbar can show just the windows on its own screen.
                    WindowFilter = screens.Length > 1 && settings.TaskbarAppsOnOwnMonitor
                        ? (Func<AppWindow, bool>)(w => OnScreen(w.Handle, device))
                        : null,
                };
                m.Taskbar.SizeWanted += LayoutAll;
                m.Taskbar.PinsChanged += () => { foreach (var o in monitors) if (o != m) o.Taskbar.Rebuild(); };
                m.SysBox = new SystemBoxForm(settings, backdrop, m.Primary ? tray : null) // tray icons: main monitor only
                {
                    OpenSettings = () => OpenSettings?.Invoke(),
                    HideBars = ToggleHidden,
                };
                m.SysBox.SizeWanted += LayoutAll;
                var tb = m.Taskbar;
                m.Strip = new StripForm(settings, backdrop) { RightClick = pt => tb.ShowBackgroundMenu(pt) };
                m.Frame = new FrameForm();
                m.StartBox = new StartBoxForm(settings, backdrop)
                {
                    OpenStartMenu = r => startMenu.Toggle(r, settings.TaskbarEdge != "Top"),
                    BeforeLeaving = PrepareForSessionEnd,
                    BackgroundMenu = pt => tb.ShowBackgroundMenu(pt),
                };
                m.StartBox.SizeWanted += LayoutAll;
                m.ForcedWorkArea = true; // reset the usable area once at start (an earlier version may have narrowed it for the frame)
                m.AppBar = new AppBarReservation();
                monitors.Add(m);
            }
            foreach (var m in monitors) m.Taskbar.Rebuild();
        }

        static bool OnScreen(IntPtr h, string device)
        {
            try { return Screen.FromHandle(h).DeviceName == device; } catch { return true; }
        }

        void DisposeMonitors()
        {
            foreach (var m in monitors)
            {
                RestoreWorkArea(m);
                m.AppBar?.Dispose();
                if (m.SideBars != null) foreach (var sb in m.SideBars) sb.Dispose();
                m.Strip?.Dispose();
                m.Frame?.Dispose();
                m.StartBox?.Dispose();
                m.Taskbar?.Dispose();
                m.SysBox?.Dispose();
            }
            monitors.Clear();
        }

        /// <summary>A monitor was plugged in/out or rearranged.</summary>
        void OnDisplaysChanged()
        {
            if (!running) return;
            if (SetupKey() == monitorSetup) LayoutAll();
            else Apply(); // rebuilds the monitor list and lays everything out again
        }

        public void OnIndexChanged()
        {
            if (running) foreach (var m in monitors) m.Taskbar.Rebuild();
        }

        // ---------- placement ----------

        void LayoutAll()
        {
            if (!running) return;
            foreach (var m in monitors.ToList()) Layout(m);
        }

        void Layout(MonitorBars m)
        {
            var scr = m.Screen.Bounds;
            bool bottom = settings.TaskbarEdge != "Top";
            int gap = (int)(settings.EdgeGap * s), between = (int)(8 * s);

            var tb = m.Taskbar.Measure();
            var sb = m.SysBox.Measure();
            int h = tb.Height;
            int y = bottom ? scr.Bottom - gap - h : scr.Top + gap;
            int margin = Math.Max(gap, (int)(8 * s));

            // The Start box takes the left corner; on the main monitor the CPU/RAM box and the player follow.
            int leftEdge = scr.X + margin;
            var stb = m.StartBox.Measure();
            int startX = leftEdge;
            if (settings.ShowStartButton) leftEdge += stb.Width + between;
            int perfX = leftEdge, mediaX = leftEdge;
            Size pb = Size.Empty, mb = Size.Empty;
            if (m.Primary)
            {
                pb = perfbox.Measure();
                mb = mediabox.Measure();
                if (PerfShown) leftEdge = perfX + pb.Width + between;
                mediaX = leftEdge;
                if (MediaShown) leftEdge = mediaX + mb.Width + between;
            }

            int tbX, sbX;
            bool beside = settings.SystemBoxPlace == "Beside";
            if (settings.TaskbarAlign == "Center")
            {
                if (beside)
                {
                    int total = tb.Width + between + sb.Width;
                    tbX = scr.X + (scr.Width - total) / 2;
                    sbX = tbX + tb.Width + between;
                }
                else
                {
                    tbX = scr.X + (scr.Width - tb.Width) / 2;
                    sbX = settings.SystemBoxPlace == "Left" ? leftEdge : scr.Right - margin - sb.Width;
                }
            }
            else
            {
                if (settings.SystemBoxPlace == "Left") { sbX = leftEdge; tbX = sbX + sb.Width + between; }
                else { tbX = leftEdge; sbX = beside ? tbX + tb.Width + between : scr.Right - margin - sb.Width; }
            }

            int thick = h + gap * 2;
            var stripRect = bottom ? new Rectangle(scr.X, scr.Bottom - thick, scr.Width, thick) : new Rectangle(scr.X, scr.Top, scr.Width, thick);
            if (m.Strip.Bounds != stripRect) m.Strip.Bounds = stripRect;
            m.Taskbar.Bounds = new Rectangle(tbX, y, tb.Width, h);
            if (FrameOn) RenderFrame(m); // after the bar height is known: the frame's band matches it
            m.SysBox.Bounds = new Rectangle(sbX, y, sb.Width, h);
            m.StartBox.Bounds = new Rectangle(startX, y, stb.Width, h);
            m.StartBox.Invalidate();
            m.Taskbar.Invalidate();
            m.SysBox.Invalidate();
            if (m.Primary)
            {
                tray?.SetRect(new Rectangle(scr.X, y, scr.Width, h));
                perfbox.Bounds = new Rectangle(perfX, y, pb.Width, h);
                mediabox.Bounds = new Rectangle(mediaX, y, mb.Width, h);
                perfbox.Invalidate();
                mediabox.Invalidate();
            }
            UpdateVisibility(m);
        }

        // ---------- visibility ----------

        /// <summary>
        /// The bottom taskbar on the screen containing <paramref name="p"/> (bounds and window), if it's
        /// showing — the attached launcher sits on top of it instead of covering it.
        /// </summary>
        public (Rectangle Bounds, IntPtr Handle)? BottomTaskbarAt(Point p)
        {
            if (userHidden || settings.TaskbarEdge == "Top") return null;
            string dev;
            try { dev = Screen.FromPoint(p).DeviceName; } catch { return null; }
            foreach (var m in monitors)
                if (m.Device == dev && !m.FullscreenHidden && m.Taskbar != null && m.Taskbar.Visible && !m.Taskbar.IsDisposed)
                    return (m.Taskbar.Bounds, m.Taskbar.Handle);
            return null;
        }

        public void ToggleHidden()
        {
            if (!running) return;
            userHidden = !userHidden;
            animateVisibility = true;
            try { foreach (var m in monitors) UpdateVisibility(m); }
            finally { animateVisibility = false; }
        }

        static void SetVisible(Form f, bool visible, bool animate = false)
        {
            if (visible)
            {
                Anim.Settle(f); // coming back while still fading out
                if (!f.Visible)
                {
                    if (animate) Anim.PopIn(f, 0, 180); // fade only: moving would re-render the backdrop every frame
                    f.Show();
                }
            }
            else if (f.Visible)
            {
                if (animate) Anim.FadeOutHide(f, 160);
                else { Anim.Settle(f); f.Hide(); }
            }
        }

        bool animateVisibility; // only the double-tap toggle animates; fullscreen apps hide the bars at once

        void UpdateVisibility(MonitorBars m)
        {
            bool show = !userHidden && !m.FullscreenHidden;
            bool a = animateVisibility;
            SetVisible(m.Taskbar, show, a);
            SetVisible(m.SysBox, show, a);
            SetVisible(m.StartBox, show && settings.ShowStartButton, a);
            if (m.Primary)
            {
                SetVisible(perfbox, show && PerfShown, a);
                SetVisible(mediabox, show && MediaShown, a);
            }

            // The frame around the screen (never animated: it's drawn with per-pixel transparency).
            // Only while no window is maximized on this screen: a maximized window fills the screen as
            // usual (the bar band stays reserved) and the frame steps aside instead of squeezing it.
            bool frame = show && FrameOn && !m.MaximizedPresent;
            if (frame && !m.Frame.Visible) { RenderFrame(m); m.Frame.Show(); KeepBelowBars(m, m.Frame.Handle); }
            else if (!frame && m.Frame.Visible) m.Frame.Hide();

            // Solid band behind the bars while a window is maximized (only meaningful when space is reserved;
            // the frame already covers it).
            bool band = show && !frame && m.MaximizedPresent && settings.FillWhenMaximized && settings.ReserveSpace;
            if (band && !m.Strip.Visible)
            {
                m.Strip.Show();
                KeepStripBelowBars(m);
            }
            else if (!band && m.Strip.Visible) m.Strip.Hide();

            // boxes sitting in the frame or the strip melt into it: no rounded edges, no shadow
            bool inBand = frame || band;
            m.Taskbar.OnBand = inBand;
            m.SysBox.OnBand = inBand;
            m.StartBox.OnBand = inBand;
            if (m.Primary)
            {
                if (perfbox != null) perfbox.OnBand = inBand;
                if (mediabox != null) mediabox.OnBand = inBand;
            }

            // Only touch the reservation when it really changes; every change makes Windows re-fit maximized windows.
            bool reserve = settings.ReserveSpace && !userHidden;
            bool bottom = settings.TaskbarEdge != "Top";
            int thickness = m.Taskbar.Height + (int)(settings.EdgeGap * s) * 2;
            var bounds = m.Screen.Bounds;
            bool sides = false; // the frame no longer reserves its thin sides: it simply hides while a window is maximized
            string key = reserve ? bounds + "|" + bottom + "|" + thickness + "|" + (sides ? FrameSide : 0) : "none";
            if (key == m.ReserveKey) return;
            m.ReserveKey = key;
            RestoreWorkArea(m); // drop anything we forced; the reservations below are the real thing
            if (reserve) m.AppBar.Set(bounds, bottom, thickness);
            else m.AppBar.Remove();
            // With the frame, its three thin sides are reserved the same way Windows' own taskbar is, so
            // Windows itself sizes maximized windows to fit inside — nothing has to be pushed around.
            m.SideBars ??= new[] { new AppBarReservation(), new AppBarReservation(), new AppBarReservation() };
            uint[] edges = bottom ? new uint[] { 0, 1, 2 } : new uint[] { 0, 3, 2 };
            for (int i = 0; i < 3; i++)
                if (sides) m.SideBars[i].Set(bounds, edges[i], FrameSide);
                else m.SideBars[i].Remove();
            m.FrameReserved = sides;
        }

        // ---------- making sure maximized windows stop at the bars ----------

        int Thickness(MonitorBars m) => m.Taskbar.Height + (int)(settings.EdgeGap * s) * 2;

        /// <summary>
        /// Makes sure Windows really keeps the strip free (checked twice a second). If the reservation was
        /// lost — Explorer restart, sleep, display change, or Explorer simply recalculating without it —
        /// it is registered again and, if that doesn't take, the usable area is set directly. Maximized
        /// windows that already reach under the bars are shrunk back.
        /// </summary>
        void EnforceReservation(MonitorBars m, bool slowTick)
        {
            if (!settings.ReserveSpace || userHidden) return;
            var bounds = m.Screen.Bounds;
            var work = WorkArea(bounds);
            if (work.IsEmpty) return;
            bool bottom = settings.TaskbarEdge != "Top";
            int limit = bottom ? bounds.Bottom - Thickness(m) : bounds.Top + Thickness(m), slack = (int)(2 * s);
            bool Ok(Rectangle w) => bottom ? w.Bottom <= limit + slack : w.Top >= limit - slack;

            if (!Ok(work))
            {
                // first just confirm our place again (cheap, and what Windows expects from an app bar)
                bool atBottom = settings.TaskbarEdge != "Top";
                m.AppBar.Set(bounds, atBottom, Thickness(m));
                work = WorkArea(bounds);
                if (Ok(work)) { Log.Throttled("appbar-confirm:" + m.Device, "Reserved strip was dropped by Windows on " + m.Device + " — confirmed it again."); return; }
            }
            if (!Ok(work))
            {
                if ((DateTime.Now - m.LastRenew).TotalMinutes > 5) // re-registering makes Windows re-fit windows: rarely
                {
                    m.LastRenew = DateTime.Now;
                    m.AppBar.Renew();
                    work = WorkArea(bounds);
                }
                if (!Ok(work))
                {
                    var desired = bottom ? Rectangle.FromLTRB(work.Left, work.Top, work.Right, limit)
                                         : Rectangle.FromLTRB(work.Left, limit, work.Right, work.Bottom);
                    m.Corrections++;
                    Log.Throttled("workarea:" + m.Device, "Reserved strip missing on " + m.Device + " (usable area was " + work +
                        ") — set it to " + desired + ". Corrections so far: " + m.Corrections + ".");
                    SetWorkArea(desired, broadcast: false);
                    m.ForcedWorkArea = true;
                    work = desired;
                }
                RefitMaximized(m, work, bottom);
            }
            // No periodic re-fitting: when the reservation is in place Windows sizes maximized windows
            // itself, and fitting them again every couple of seconds made some apps (browsers) bounce
            // between our size and theirs.
        }

        readonly Dictionary<IntPtr, string> fittedOnce = new Dictionary<IntPtr, string>();

        // Windows that just got maximized are checked once, a moment later (apps finish sizing themselves
        // first). Some apps maximize to the whole monitor instead of the usable area — those get fitted.
        // Once per maximize, never repeatedly, so nothing bounces.
        readonly HashSet<IntPtr> seenMaximized = new HashSet<IntPtr>();
        readonly Dictionary<IntPtr, DateTime> fitDue = new Dictionary<IntPtr, DateTime>();
        Timer fitTimer;

        void WatchNewlyMaximized()
        {
            if (!settings.ReserveSpace || userHidden) return;
            var still = new HashSet<IntPtr>();
            foreach (var w in tracker.Windows)
            {
                IntPtr h = w.Handle;
                if (!IsZoomed(h) || Native.IsIconic(h)) continue;
                still.Add(h);
                if (seenMaximized.Add(h)) fitDue[h] = DateTime.Now.AddMilliseconds(250);
            }
            seenMaximized.RemoveWhere(h => !still.Contains(h)); // restored or closed: next maximize counts again
            if (fitDue.Count > 0)
            {
                if (fitTimer == null) { fitTimer = new Timer { Interval = 100 }; fitTimer.Tick += (o, e) => FitDue(); }
                fitTimer.Start();
            }
        }

        void FitDue()
        {
            var now = DateTime.Now;
            bool bottom = settings.TaskbarEdge != "Top";
            foreach (var h in new List<IntPtr>(fitDue.Keys))
            {
                if (fitDue[h] > now) continue;
                fitDue.Remove(h);
                if (!IsZoomed(h) || Native.IsIconic(h)) continue;
                var w = tracker.Windows.FirstOrDefault(x => x.Handle == h);
                if (w == null) continue;
                foreach (var m in monitors)
                {
                    if (!OnScreen(h, m.Device)) continue;
                    var b = m.Screen.Bounds;
                    var work = WorkArea(b);
                    if (work.IsEmpty) break;
                    // even if Windows lost the reserved strip for a moment, fit to where the bars really are
                    if (bottom) work = Rectangle.FromLTRB(work.Left, work.Top, work.Right, Math.Min(work.Bottom, b.Bottom - Thickness(m)));
                    else work = Rectangle.FromLTRB(work.Left, Math.Max(work.Top, b.Top + Thickness(m)), work.Right, work.Bottom);
                    FitOne(w, m, work, bottom);
                    break;
                }
            }
            if (fitDue.Count == 0) fitTimer.Stop();
        }

        // windows we had to shrink: if one keeps growing back, leave it alone instead of fighting (that flickers)
        readonly Dictionary<IntPtr, (int count, DateTime since)> refits = new Dictionary<IntPtr, (int, DateTime)>();

        /// <summary>Shrinks maximized windows on this monitor that reach under the bars.</summary>
        void RefitMaximized(MonitorBars m, Rectangle work, bool bottom)
        {
            foreach (var w in tracker.Windows)
            {
                IntPtr h = w.Handle;
                if (refits.TryGetValue(h, out var rf) && rf.count >= 3 && (DateTime.Now - rf.since).TotalMinutes < 10) continue;
                if (!IsZoomed(h) || Native.IsIconic(h) || !OnScreen(h, m.Device)) continue;
                // each window at most once per usable-area correction (never back and forth)
                string fitKey = m.Device + work;
                if (fittedOnce.TryGetValue(h, out var fk) && fk == fitKey) continue;
                fittedOnce[h] = fitKey;
                if (fittedOnce.Count > 300) fittedOnce.Clear();
                FitOne(w, m, work, bottom);
            }
        }

        /// <summary>Shrinks one maximized window back above the bars if it reaches under them.</summary>
        void FitOne(AppWindow w, MonitorBars m, Rectangle work, bool bottom)
        {
            IntPtr h = w.Handle;
            {
                if (refits.TryGetValue(h, out var rf) && rf.count >= 3 && (DateTime.Now - rf.since).TotalMinutes < 10) return;
                if ((Native.GetWindowLong(h, -16) & 0x40000 /* WS_THICKFRAME */) == 0) return; // not a normal resizable window (e.g. fullscreen video)
                if (!Native.GetWindowRect(h, out var rr)) return;
                var r = rr.ToRectangle();
                int frame = Math.Max(0, work.Left - r.Left);     // maximized windows hang a few invisible pixels past the edges
                int tol = frame + (int)(2 * s);
                const uint flags = 0x4 | 0x10 | 0x200 | 0x4000; // NOZORDER | NOACTIVATE | NOOWNERZORDER | ASYNCWINDOWPOS
                bool misfit = bottom ? r.Bottom > work.Bottom + tol : r.Top < work.Top - tol;
                if (misfit)
                {
                    if (!refits.TryGetValue(h, out rf) || (DateTime.Now - rf.since).TotalMinutes >= 10) rf = (0, DateTime.Now);
                    refits[h] = rf = (rf.count + 1, rf.since);
                    if (rf.count >= 3)
                    {
                        Log.Write("Stopped shrinking " + ProcessName(w) + " — it keeps resizing itself back (avoids flicker).");
                        return;
                    }
                }
                if (refits.Count > 200) refits.Clear();
                if (bottom && r.Bottom > work.Bottom + tol)
                {
                    Log.Throttled("refit:" + m.Device, "Shrank a maximized window (" + ProcessName(w) + ") that reached under the bars.");
                    Native.SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, work.Bottom + frame - r.Y, flags);
                }
                else if (!bottom && r.Top < work.Top - tol)
                {
                    Log.Throttled("refit:" + m.Device, "Moved a maximized window (" + ProcessName(w) + ") out from under the bars.");
                    int top = work.Top - frame;
                    Native.SetWindowPos(h, IntPtr.Zero, r.X, top, r.Width, r.Bottom - top, flags);
                }
            }
        }

        static string ProcessName(AppWindow w) => w.ExePath == null ? "?" : System.IO.Path.GetFileName(w.ExePath);

        bool FrameOn => settings.ScreenFrame;
        int FrameSide => (int)(settings.FrameThickness * s);
        int FrameRadius => (int)(16 * s);

        Rectangle FrameInner(MonitorBars m) => FrameForm.Inner(m.Screen.Bounds, FrameSide, Thickness(m), settings.TaskbarEdge != "Top");

        void RenderFrame(MonitorBars m)
        {
            try { m.Frame.Render(m.Screen.Bounds, FrameInner(m), FrameRadius, backdrop, settings.Theme, settings.ShowImage, settings.Opacity); }
            catch (Exception ex) { Log.Error("Frame.Render", ex); }
        }

        /// <summary>The inner edge of the frame's bar band on the screen containing <paramref name="p"/>, if the frame shows.</summary>
        /// <summary>Top of the band the bars sit in (the frame's band, or the strip while a window is maximized).</summary>
        public int? BandTopAt(Point p)
        {
            if (!running || userHidden || settings.TaskbarEdge == "Top") return null;
            string dev;
            try { dev = Screen.FromPoint(p).DeviceName; } catch { return null; }
            foreach (var m in monitors)
                if (m.Device == dev)
                {
                    if (m.Frame.Visible) return FrameInner(m).Bottom;
                    if (m.Strip.Visible) return m.Strip.Top;
                }
            return null;
        }

        public int? FrameEdgeAt(Point p)
        {
            if (!running || !FrameOn || userHidden || settings.TaskbarEdge == "Top") return null;
            string dev;
            try { dev = Screen.FromPoint(p).DeviceName; } catch { return null; }
            foreach (var m in monitors)
                if (m.Device == dev && m.Frame.Visible) return FrameInner(m).Bottom;
            return null;
        }


        void RestoreWorkArea(MonitorBars m)
        {
            if (!m.ForcedWorkArea) return;
            m.ForcedWorkArea = false;
            SetWorkArea(m.Screen.Bounds, broadcast: true); // Explorer recalculates the rest itself
        }

        static Rectangle WorkArea(Rectangle screen)
        {
            var mon = MonitorFromPoint(new POINT { x = screen.X + screen.Width / 2, y = screen.Y + screen.Height / 2 }, 2 /* NEAREST */);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            return GetMonitorInfo(mon, ref mi) ? mi.rcWork.ToRectangle() : Rectangle.Empty;
        }

        /// <param name="broadcast">
        /// Announce the change to all apps. Not done while enforcing: Explorer reacts to the announcement by
        /// recalculating (without our strip), which would undo it again straight away.
        /// </param>
        static void SetWorkArea(Rectangle r, bool broadcast)
        {
            var rc = new Native.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
            SystemParametersInfo(0x2F /* SPI_SETWORKAREA */, 0, ref rc, broadcast ? 0x2u /* SPIF_SENDCHANGE */ : 0u);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public Native.RECT rcMonitor, rcWork; public uint dwFlags; }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref Native.RECT value, uint winIni);

        void Poll()
        {
            if (!running) return;
            if (pollCount % 600 == 0) Log.Write("Status: " + monitors.Count + " monitor(s), " + Log.Resources()); // every 5 min
            if ((DateTime.Now - tracker.LastRefresh).TotalMilliseconds >= 250) tracker.Refresh(); // skip if a window event just did it
            if (++pollCount % 4 == 0) ExplorerTaskbar.EnsureHidden(); // every 2 s is plenty

            IntPtr fg = Native.GetForegroundWindow();
            string fgScreen = null;
            bool fullscreen = IsFullscreenApp(fg);
            try { if (fullscreen) fgScreen = Screen.FromHandle(fg).DeviceName; } catch { }

            bool slowTick = pollCount % 4 == 0;
            foreach (var m in monitors)
            {
                // Watchdog: bars that should be visible must really be visible (not minimized / hidden / buried).
                if (!userHidden && !m.FullscreenHidden)
                {
                    // While the full-width strip shows, it must stay *below* the bars — if Windows reorders
                    // the always-on-top windows, the strip would cover them and the bar looks empty.
                    if (m.Strip.Visible) KeepStripBelowBars(m);
                    if (m.Frame.Visible) KeepBelowBars(m, m.Frame.Handle);
                    m.Taskbar.EnsureShown(slowTick);
                    m.SysBox.EnsureShown(slowTick);
                    if (settings.ShowStartButton) m.StartBox.EnsureShown(slowTick);
                    if (m.Primary)
                    {
                        if (PerfShown) perfbox.EnsureShown(slowTick);
                        if (MediaShown) mediabox.EnsureShown(slowTick);
                    }
                }
                EnforceReservation(m, slowTick);
            }
            CheckScreens(refreshWindows: false, fg, fullscreen, fgScreen);
        }

        /// <summary>
        /// Fullscreen apps and maximized windows, per screen. Runs on every window event (so it reacts
        /// instantly) and on the poll as a fallback.
        /// </summary>
        void CheckScreens(bool refreshWindows = true, IntPtr fg = default, bool? fullscreenKnown = null, string fgScreen = null)
        {
            // the window list is re-read at most every 100 ms (dragging a window sends a stream of events);
            // whether a window is maximized is asked live, so that part is never stale
            if (refreshWindows && (DateTime.Now - tracker.LastRefresh).TotalMilliseconds >= 100) tracker.Refresh();
            bool fullscreen;
            if (fullscreenKnown.HasValue) fullscreen = fullscreenKnown.Value;
            else
            {
                fg = Native.GetForegroundWindow();
                fullscreen = IsFullscreenApp(fg);
                try { if (fullscreen) fgScreen = Screen.FromHandle(fg).DeviceName; } catch { }
            }

            WatchNewlyMaximized();
            var now = DateTime.Now;
            foreach (var m in monitors)
            {
                // Hide for a fullscreen app only on its own screen, once it has stayed fullscreen for a
                // blink (~0.1 s) — a window passing through a fullscreen state doesn't count.
                bool fsNow = fullscreen && (fgScreen == null ? m.Primary : fgScreen == m.Device);
                if (!fsNow) m.FullscreenSince = DateTime.MinValue;
                else if (m.FullscreenSince == DateTime.MinValue) m.FullscreenSince = now;
                bool fs = fsNow && (m.FullscreenHidden || (now - m.FullscreenSince).TotalMilliseconds >= FullscreenConfirmMs);
                if (fsNow && !fs) confirm.Start(); // look again in a moment
                if (fs != m.FullscreenHidden)
                    Log.Throttled("fs:" + m.Device + fs, (fs ? "Bars hidden on " : "Bars shown again on ") + m.Device +
                        (fs ? " for a fullscreen app (" + ProcessName(fg) + ")." : "."));
                // the strip behind the bars follows maximized windows straight away
                bool max = AnyMaximizedWindow(m.Device);
                if (fs != m.FullscreenHidden || max != m.MaximizedPresent)
                {
                    m.FullscreenHidden = fs;
                    m.MaximizedPresent = max;
                    UpdateVisibility(m);
                }
            }
        }

        const int FullscreenConfirmMs = 100;
        readonly Timer confirm = new Timer { Interval = FullscreenConfirmMs + 10 };

        /// <summary>
        /// The full-width strip must sit *below* the bars. Instead of raising the bars (which would also
        /// push them over other apps' menus and popups), the strip is moved down to just under them.
        /// </summary>
        /// <summary>Puts <paramref name="win"/> just under the bars (same idea as the strip).</summary>
        void KeepBelowBars(MonitorBars m, IntPtr win)
        {
            var bars = new List<Form> { m.Taskbar, m.SysBox, m.StartBox };
            if (m.Primary) { bars.Add(perfbox); bars.Add(mediabox); }
            if (m.Strip.Visible) bars.Add(m.Strip);
            for (int pass = 0; pass < 5; pass++)
            {
                bool moved = false;
                foreach (var f in bars)
                {
                    if (!f.Visible || !IsAbove(win, f.Handle)) continue;
                    Native.SetWindowPos(win, f.Handle, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200);
                    moved = true;
                }
                if (!moved) break;
            }
        }

        void KeepStripBelowBars(MonitorBars m)
        {
            var bars = new List<Form> { m.Taskbar, m.SysBox, m.StartBox };
            if (m.Primary) { bars.Add(perfbox); bars.Add(mediabox); }
            for (int pass = 0; pass < 4; pass++)
            {
                bool moved = false;
                foreach (var f in bars)
                {
                    if (!f.Visible || !IsAbove(m.Strip.Handle, f.Handle)) continue;
                    Native.SetWindowPos(m.Strip.Handle, f.Handle /* insert just below it */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200);
                    moved = true;
                }
                if (!moved) break;
                if (pass == 0) Log.Throttled("strip-above", "Watchdog: the strip was above the bars — moved it underneath.");
            }
        }

        /// <summary>Is window <paramref name="a"/> above window <paramref name="b"/> in the z-order?</summary>
        static bool IsAbove(IntPtr a, IntPtr b)
        {
            IntPtr h = a;
            for (int i = 0; i < 500 && h != IntPtr.Zero; i++)
            {
                h = Native.GetWindow(h, 3 /* GW_HWNDPREV: the window above */);
                if (h == b) return false; // b is above a
            }
            return true;
        }

        /// <summary>Is any (non-minimized) window maximized on this monitor?</summary>
        bool AnyMaximizedWindow(string device)
        {
            foreach (var w in tracker.Windows)
                if (IsZoomed(w.Handle) && !Native.IsIconic(w.Handle) && OnScreen(w.Handle, device))
                    return true;
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsZoomed(IntPtr h);

        static readonly uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>
        /// Games, videos and presentations in fullscreen hide the bars: exclusive fullscreen and presentation
        /// mode as Windows reports them, or the front window covering its whole monitor without being an
        /// ordinary maximized window. A maximized window that only covers the screen because the reserved
        /// strip got lost is not a fullscreen app.
        /// </summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);

        static bool IsFullscreenApp(IntPtr fg)
        {
            try
            {
                if (SHQueryUserNotificationState(out int state) == 0 &&
                    (state == 3 /* D3D exclusive fullscreen */ || state == 4 /* presentation mode */)) return true;
                // Windows' "busy" signal isn't reliable while WispR stands in for the taskbar, so look
                // at the window itself: it must cover its whole monitor…  (cheap checks first)
                if (fg == IntPtr.Zero || Native.IsIconic(fg)) return false;
                if (!Native.GetWindowRect(fg, out var rr)) return false;
                var r = rr.ToRectangle();
                var b = Screen.FromHandle(fg).Bounds;
                if (!(r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom)) return false;
                // …and be fullscreen rather than maximized: browsers' video fullscreen and games aren't
                // "maximized" or have no title bar; a maximized window only covers the screen when the
                // reserved space got lost, and that's not a reason to hide the bars.
                long style = Native.GetWindowLong(fg, -16);
                bool hasCaption = (style & 0x00C00000) == 0x00C00000; // WS_CAPTION
                if (IsZoomed(fg) && hasCaption) return false;
                string cls = Native.GetClass(fg);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false; // the desktop
                if (!Native.IsWindowVisible(fg)) return false;
                // anything living inside the desktop (Wallpaper Engine draws there) is the desktop too
                for (IntPtr p = GetParent(fg); p != IntPtr.Zero; p = GetParent(p))
                {
                    string pc = Native.GetClass(p);
                    if (pc == "Progman" || pc == "WorkerW") return false;
                }
                uint pid = Native.GetPid(fg);
                if (pid == ownPid) return false;
                // Explorer's own full-screen overlays (Alt+Tab, Task view, the desktop) aren't apps
                string exe = System.IO.Path.GetFileName(Native.GetProcessPath(pid) ?? "");
                return !(exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                         exe.StartsWith("wallpaper32", StringComparison.OrdinalIgnoreCase) ||   // Wallpaper Engine's
                         exe.StartsWith("wallpaper64", StringComparison.OrdinalIgnoreCase) ||   // wallpaper windows
                         exe.StartsWith("webwallpaper", StringComparison.OrdinalIgnoreCase) ||
                         exe.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase) ||
                         exe.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase) ||
                         exe.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        static string ProcessName(IntPtr h)
        {
            try { return System.IO.Path.GetFileName(Native.GetProcessPath(Native.GetPid(h)) ?? "?"); } catch { return "?"; }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        // with the top drop-down on, media and performance live there instead of on the bar
        bool MediaShown => settings.ShowMediaBox && !settings.TopPanel && mediabox != null && mediabox.HasMedia;
        bool PerfShown => settings.ShowPerfBox && !settings.TopPanel;
        TopPanelForm topPanel;

        /// <summary>Where the top drop-down hangs on a screen (below the frame), or null when it shouldn't open there.</summary>
        int? TopEdgeFor(Screen scr)
        {
            if (!running || userHidden) return null;
            foreach (var m in monitors)
                if (m.Device == scr.DeviceName)
                {
                    if (m.FullscreenHidden) return null; // never over a game or video
                    bool topBar = settings.TaskbarEdge == "Top";
                    if (topBar) return null;             // the bars are up there
                    return scr.Bounds.Top + (FrameOn && m.Frame.Visible ? FrameSide : 0);
                }
            return null;
        }

        /// <summary>
        /// Where the timers widget hangs on a screen: the inner right edge (inside the frame), or null over a
        /// fullscreen game or video. With the bars off it's simply the screen's right edge.
        /// </summary>
        public int? RightEdgeFor(Screen scr)
        {
            if (!running) return scr.Bounds.Right;
            foreach (var m in monitors)
                if (m.Device == scr.DeviceName)
                {
                    if (m.FullscreenHidden) return null;
                    return scr.Bounds.Right - (FrameOn && m.Frame.Visible ? FrameSide : 0);
                }
            return scr.Bounds.Right;
        }

        /// <summary>Brings the app that's playing to the front (or starts it).</summary>
        bool ActivateMediaApp(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            string id = appId.ToLowerInvariant(), idExe = id.EndsWith(".exe") ? id : id + ".exe";
            foreach (var w in tracker.Windows)
            {
                string exe = w.ExePath == null ? "" : System.IO.Path.GetFileName(w.ExePath);
                if ((w.Aumid != null && w.Aumid.ToLowerInvariant() == id) || exe == idExe)
                {
                    Native.Activate(w.Handle);
                    return true;
                }
            }
            try { Launcher.Start(@"shell:AppsFolder\" + appId, null, null); return true; } catch { return false; }
        }

        /// <summary>Shutting down / signing out: give Windows its taskbar setting back first.</summary>
        public void PrepareForSessionEnd()
        {
            if (!running) return;
            try
            {
                foreach (var m in monitors) { m.AppBar?.Remove(); RestoreWorkArea(m); }
                ExplorerTaskbar.Restore(settings);
            }
            catch { }
        }

        static void Later(Action a)
        {
            var t = new Timer { Interval = 50 };
            t.Tick += (o, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        public void Dispose()
        {
            if (running) Stop();
            poll.Dispose();
            quickCheck.Dispose();
        }
    }
}
