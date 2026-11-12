using System;
using System.Collections.Generic;

namespace TwoAnEnd.Playtest
{
    /// A run of frames boiled down. The lows are the numbers players actually feel: a game
    /// averaging 120 fps with a 1% low of 20 stutters, one averaging 70 with a 1% low of 60
    /// doesn't.
    public struct PerfSummary
    {
        public int frames;
        public float avgMs, avgFps;
        /// 1000 / the mean of the slowest 1% (and 0.1%) of frames.
        public float low1Fps, low01Fps;
        public float p50Ms, p95Ms, p99Ms, maxMs;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "frames", frames }, { "fps", avgFps }, { "avg_ms", avgMs },
            { "low1_fps", low1Fps }, { "low01_fps", low01Fps },
            { "p50_ms", p50Ms }, { "p95_ms", p95Ms }, { "p99_ms", p99Ms }, { "max_ms", maxMs },
        };
    }

    public static class PerfStats
    {
        public static PerfSummary Summarise(List<float> frameMs)
        {
            var s = new PerfSummary();
            int n = frameMs?.Count ?? 0;
            if (n == 0) return s;
            var sorted = new float[n];
            frameMs.CopyTo(sorted);
            Array.Sort(sorted);
            double sum = 0;
            for (int i = 0; i < n; i++) sum += sorted[i];
            s.frames = n;
            s.avgMs = (float)(sum / n);
            s.avgFps = s.avgMs > 0f ? 1000f / s.avgMs : 0f;
            s.low1Fps = LowFps(sorted, 0.01);
            s.low01Fps = LowFps(sorted, 0.001);
            s.p50Ms = Percentile(sorted, 0.50);
            s.p95Ms = Percentile(sorted, 0.95);
            s.p99Ms = Percentile(sorted, 0.99);
            s.maxMs = sorted[n - 1];
            return s;
        }

        /// Nearest-rank percentile of an ascending array.
        public static float Percentile(float[] sortedAsc, double q)
        {
            int n = sortedAsc.Length;
            if (n == 0) return 0f;
            int rank = (int)Math.Ceiling(q * n) - 1;
            return sortedAsc[Math.Max(0, Math.Min(n - 1, rank))];
        }

        /// 1000 / the mean frame time of the slowest `fraction` of frames (at least one frame).
        public static float LowFps(float[] sortedAsc, double fraction)
        {
            int n = sortedAsc.Length;
            if (n == 0) return 0f;
            int k = Math.Max(1, (int)Math.Ceiling(n * fraction));
            double sum = 0;
            for (int i = n - k; i < n; i++) sum += sortedAsc[i];
            double mean = sum / k;
            return mean > 0 ? (float)(1000.0 / mean) : 0f;
        }
    }
}
