using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Tiny animation engine shared by every window: one frame clock, in step with the screen's refresh
    /// rate, drives all running tweens and per-frame callbacks, and sleeps when nothing moves, so
    /// animations cost nothing while idle.
    /// </summary>
    static class Anim
    {
        sealed class Tween
        {
            public object Key;
            public DateTime Start;
            public double Ms;
            public Func<double, double> Ease;
            public Action<double> Step;
            public Action Done;
        }

        static readonly List<Tween> tweens = new List<Tween>();
        static readonly Dictionary<object, Func<bool>> frames = new Dictionary<object, Func<bool>>();
        static readonly Dictionary<Form, double> targets = new Dictionary<Form, double>();

        // ---------- the frame clock ----------
        // Animations advance once per screen refresh: a background thread waits for the compositor
        // (DwmFlush) and asks the UI thread for a frame. Only one frame is ever queued, so a busy UI
        // thread just skips frames instead of piling them up; with nothing animating the thread sleeps.

        static Control marshal;                 // posts frames to the UI thread
        static Thread pump;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static volatile bool running;
        static int queued;
        static DateTime lastFrame = DateTime.MinValue;

        /// <summary>How long the current frame is compared with a 60 Hz one (for per-frame steps).</summary>
        public static float Dt { get; private set; } = 1f;

        /// <summary>A per-frame step scaled to the frame's real length (so speeds are the same at any refresh rate).</summary>
        public static float PerFrame(float perSixtieth) => perSixtieth * Dt;

        static void EnsureRunning()
        {
            if (marshal == null)
            {
                marshal = new Control();
                marshal.CreateControl();
                _ = marshal.Handle;
            }
            if (pump == null)
            {
                pump = new Thread(PumpLoop) { IsBackground = true, Name = "WispR frame clock", Priority = ThreadPriority.AboveNormal };
                pump.Start();
            }
            if (!running) { running = true; lastFrame = DateTime.MinValue; wake.Set(); }
        }

        static bool dwmOk = true;

        static void PumpLoop()
        {
            while (true)
            {
                if (!running) wake.WaitOne();
                if (!running) continue;
                bool waited = false;
                if (dwmOk)
                {
                    try { waited = DwmFlush() == 0; }
                    catch { dwmOk = false; }
                }
                if (!waited) Thread.Sleep(8);
                if (Interlocked.CompareExchange(ref queued, 1, 0) != 0) continue; // the last frame is still waiting
                try { marshal.BeginInvoke((Action)Frame); }
                catch { Interlocked.Exchange(ref queued, 0); Thread.Sleep(50); }
            }
        }

        static void Frame()
        {
            Interlocked.Exchange(ref queued, 0);
            var now = DateTime.Now;
            Dt = lastFrame == DateTime.MinValue ? 1f : (float)Math.Max(0.2, Math.Min(3.0, (now - lastFrame).TotalMilliseconds / 16.667));
            lastFrame = now;

            foreach (var t in tweens.ToArray())
            {
                if (!tweens.Contains(t)) continue; // replaced by an earlier step this frame
                double p = Math.Min(1, (now - t.Start).TotalMilliseconds / t.Ms);
                try { t.Step(t.Ease(p)); }
                catch (Exception ex) { Log.Error("Anim", ex); p = 1; }
                if (p >= 1 && tweens.Remove(t))
                    try { t.Done?.Invoke(); } catch (Exception ex) { Log.Error("Anim.Done", ex); }
            }
            foreach (var kv in new List<KeyValuePair<object, Func<bool>>>(frames))
            {
                if (!frames.TryGetValue(kv.Key, out var f) || f != kv.Value) continue;
                bool more;
                try { more = f(); }
                catch (Exception ex) { Log.Error("Anim.Frame", ex); more = false; }
                if (!more && frames.TryGetValue(kv.Key, out var f2) && f2 == f) frames.Remove(kv.Key);
            }
            if (tweens.Count == 0 && frames.Count == 0) running = false;
        }

        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        // ---------- tweens and per-frame callbacks ----------

        /// <summary>
        /// Runs <paramref name="step"/> with an eased 0→1 value over <paramref name="ms"/> milliseconds.
        /// A new animation with the same <paramref name="key"/> replaces the running one (its "done" is skipped).
        /// </summary>
        public static void Run(object key, double ms, Action<double> step, Action done = null, Func<double, double> ease = null)
        {
            tweens.RemoveAll(t => Equals(t.Key, key));
            var tw = new Tween { Key = key, Start = DateTime.Now, Ms = Math.Max(1, ms), Ease = ease ?? OutCubic, Step = step, Done = done };
            tweens.Add(tw);
            try { step(0); } catch { }
            EnsureRunning();
        }

        /// <summary>
        /// Calls <paramref name="step"/> once per screen refresh until it returns false (or <see cref="StopFrames"/>).
        /// Registering again with the same key replaces the callback.
        /// </summary>
        public static void Frames(object key, Func<bool> step)
        {
            frames[key] = step;
            EnsureRunning();
        }

        public static void StopFrames(object key) => frames.Remove(key);
        public static bool FramesRunning(object key) => frames.ContainsKey(key);

        public static void Stop(object key) => tweens.RemoveAll(t => Equals(t.Key, key));
        public static bool IsRunning(object key) => tweens.Exists(t => Equals(t.Key, key));

        public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
        public static double OutQuint(double t) => 1 - Math.Pow(1 - t, 5);
        public static double InCubic(double t) => t * t * t;
        /// <summary>Ends with a small, soft overshoot — for things that "land".</summary>
        public static double OutBack(double t) { const double c = 1.4; double u = t - 1; return 1 + (c + 1) * u * u * u + c * u * u; }

        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>
        /// Popup entrance: call right before <c>Show()</c>. The window fades in while gliding
        /// <paramref name="dy"/> pixels into place (positive = comes up from below).
        /// </summary>
        public static void PopIn(Form f, int dy, double ms = 150)
        {
            // if it's still fading in from a previous open, aim for that open's opacity, not the halfway value
            double target = IsRunning(f) && targets.TryGetValue(f, out var t0) ? t0 : f.Opacity;
            targets[f] = target;
            int finalTop = f.Top;
            f.Opacity = 0;
            Run(f, ms, e =>
            {
                if (f.IsDisposed) return;
                f.Opacity = Math.Max(0.01, target * e);
                f.Top = finalTop + (int)Math.Round(dy * (1 - e));
            }, () =>
            {
                targets.Remove(f);
                if (f.IsDisposed) return;
                f.Opacity = target;
                f.Top = finalTop;
            });
        }

        /// <summary>Fades a window out, then hides it (its opacity is put back for next time).</summary>
        public static void FadeOutHide(Form f, double ms = 150)
        {
            double target = IsRunning(f) && targets.TryGetValue(f, out var t0) ? t0 : f.Opacity;
            targets[f] = target;
            double from = f.Opacity;
            Run(f, ms, e => { if (!f.IsDisposed) f.Opacity = Math.Max(0.01, from * (1 - e)); }, () =>
            {
                targets.Remove(f);
                if (f.IsDisposed) return;
                f.Hide();
                f.Opacity = target;
            });
        }

        /// <summary>Finishes a running pop-in at once (before the window is moved somewhere else).</summary>
        public static void Settle(Form f)
        {
            if (!IsRunning(f)) return;
            Stop(f);
            if (targets.TryGetValue(f, out var t) && !f.IsDisposed) f.Opacity = t;
            targets.Remove(f);
        }

        /// <summary>Smoothly follows a target value (for hover highlights etc.). Returns true while still moving.</summary>
        public static bool Approach(ref float value, float target, float perTick)
        {
            if (Math.Abs(value - target) <= perTick) { bool moved = value != target; value = target; return moved; }
            value += Math.Sign(target - value) * perTick;
            return true;
        }
    }
}
