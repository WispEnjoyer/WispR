using System;

namespace WispR
{
    /// <summary>
    /// WispR's own clock for where the current song is. Media apps report their position only now and
    /// then (on play, pause or seek) and some of those reports are junk — Firefox-based browsers playing
    /// YouTube sometimes drop the timing ("0 of 0") or repeat an old position. So the playhead runs on
    /// its own while the song plays and only moves when a report really says something new:
    /// <list type="bullet">
    /// <item>a report with a fresh timestamp, or a changed position;</item>
    /// <item>play / pause (the clock stops and starts with it);</item>
    /// <item>a seek made in WispR (reports that disagree right afterwards are stale and ignored).</item>
    /// </list>
    /// A report of "no timing at all" never moves it, a sudden jump back to the very start has to be
    /// confirmed by a second report first, and a song's length, once known, is kept.
    /// </summary>
    sealed class Playhead
    {
        string key;
        long anchorPos;            // position (ticks) at anchorAt
        DateTime anchorAt;
        bool playing;
        long duration;             // 0 = not known (yet)
        bool canSeek;
        long lastUpdated = long.MinValue, lastPos = long.MinValue;
        DateTime guardUntil; long guardTarget;
        long pendingResetPos = -1; DateTime pendingResetAt;

        static readonly long Second = TimeSpan.TicksPerSecond;

        public long Duration => duration;
        public bool CanSeek => canSeek;

        long Expected(DateTime now)
        {
            long p = anchorPos + (playing ? (now - anchorAt).Ticks : 0);
            if (p < 0) p = 0;
            if (duration > 0 && p > duration) p = duration;
            return p;
        }

        void Anchor(long pos, DateTime at) { anchorPos = Math.Max(0, pos); anchorAt = at; pendingResetPos = -1; }

        /// <summary>A seek made by WispR: jump there now and distrust reports that disagree for a moment.</summary>
        public void Seek(long pos, DateTime now)
        {
            Anchor(pos, now);
            guardTarget = anchorPos;
            guardUntil = now.AddSeconds(2.5);
        }

        /// <summary>
        /// Feeds one reading. <paramref name="timingOk"/>: the app sent a timeline at all.
        /// <paramref name="updated"/> is the app's own timestamp for <paramref name="pos"/> (a FILETIME, 0 = none).
        /// Returns where the song is now, and when that was true.
        /// </summary>
        public (long pos, DateTime at) Update(string songKey, bool isPlaying, bool timingOk, long pos, long updated,
                                              long length, bool seekable, DateTime now)
        {
            bool reportHasTiming = timingOk && (length > 0 || pos > 0); // "0 of 0" = the app dropped the timing

            if (songKey != key)
            {
                key = songKey;
                duration = 0; canSeek = false;
                guardUntil = DateTime.MinValue;
                playing = isPlaying;
                lastUpdated = updated; lastPos = pos;
                Anchor(reportHasTiming ? Candidate(pos, updated, isPlaying, now, true) : 0, now);
                if (length > 0) { duration = length; canSeek = seekable; }
                return (Expected(now), now);
            }

            // play / pause: the clock stops or starts here
            if (isPlaying != playing)
            {
                Anchor(Expected(now), now);
                playing = isPlaying;
            }

            if (length > 0) { duration = length; canSeek = seekable; } // the length never changes within a song

            if (reportHasTiming && (updated != lastUpdated || pos != lastPos))
            {
                bool fresh = updated != lastUpdated && updated > 0;
                long cand = Candidate(pos, updated, playing, now, fresh);
                lastUpdated = updated; lastPos = pos;
                long expected = Expected(now);

                if (now < guardUntil && Math.Abs(cand - guardTarget) > 3 * Second)
                {
                    // right after our own seek: an old report — ignore it
                }
                else if (cand < 2 * Second && expected > 5 * Second)
                {
                    // back to the very start? Often that's a junk report. Believe it only when a second
                    // report agrees (it moved on from there as a real restart would).
                    if (pendingResetPos >= 0 && Math.Abs(cand - (pendingResetPos + (playing ? (now - pendingResetAt).Ticks : 0))) < 2 * Second && cand != pendingResetPos)
                        Anchor(cand, now);
                    else { pendingResetPos = cand; pendingResetAt = now; }
                }
                else if (fresh || Math.Abs(cand - expected) > 2 * Second)
                {
                    Anchor(cand, now); // a real update or a seek made in the app
                }
            }
            return (Expected(now), now);
        }

        /// <summary>Where a report puts the song now: from the app's timestamp when it's believable, otherwise as of now.</summary>
        long Candidate(long pos, long updated, bool isPlaying, DateTime now, bool trustTimestamp)
        {
            if (!trustTimestamp || updated <= 0 || !isPlaying) return pos;
            DateTime at;
            try { at = DateTime.FromFileTimeUtc(updated); } catch { return pos; }
            var age = now - at;
            if (age < TimeSpan.FromSeconds(-1) || age > TimeSpan.FromHours(6)) return pos; // a clock that makes no sense
            long p = pos + Math.Max(0, age.Ticks);
            return duration > 0 ? Math.Min(p, duration) : p;
        }
    }
}
