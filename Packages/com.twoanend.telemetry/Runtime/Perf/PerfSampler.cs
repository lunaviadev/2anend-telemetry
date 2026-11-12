using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace TwoAnEnd.Playtest
{
    /// Watches every frame: its time, the CPU (main / render thread) and GPU time from
    /// FrameTimingManager, garbage allocated, draw calls, memory. Rolls a window of frames up
    /// into a "perf" event, keeps the whole session for the end-of-session summary, and a
    /// short ring of recent frames for the overlay's graph.
    ///
    /// CPU / GPU timings need Player Settings > Other > Frame Timing Stats on; draw-call
    /// counters only exist in the editor and development builds. Anything a platform can't
    /// measure is left out of the event rather than logged as a fake zero.
    public sealed class PerfSampler : IDisposable
    {
        public const int RingSize = 300;
        public readonly float[] Ring = new float[RingSize];
        public int RingHead;

        public float LastFps, LastLow1, LastCpuMs, LastGpuMs, LastMemMb;
        /// frames slower than this (ms) count as hitches; set from the config
        public float HitchMs = 50f;

        private readonly List<float> _window = new List<float>(2048);
        private readonly List<float> _session = new List<float>(1 << 16);
        private const int MaxSessionFrames = 4_000_000;
        private readonly FrameTiming[] _ft = new FrameTiming[1];
        private double _cpu, _gpu, _main, _render;
        private int _timings, _hitches, _gcBase, _gcWindowBase;
        private long _gcAllocWindow, _peakMem;
        private ProfilerRecorder _gcAlloc, _draws, _batches, _setPass, _tris, _sysMem;

        public int SessionHitches { get; private set; }
        public long PeakMemBytes => _peakMem;

        public void Begin()
        {
            _gcAlloc = Start(ProfilerCategory.Memory, "GC Allocated In Frame");
            _sysMem = Start(ProfilerCategory.Memory, "System Used Memory");
            _draws = Start(ProfilerCategory.Render, "Draw Calls Count");
            _batches = Start(ProfilerCategory.Render, "Batches Count");
            _setPass = Start(ProfilerCategory.Render, "SetPass Calls Count");
            _tris = Start(ProfilerCategory.Render, "Triangles Count");
            _gcBase = _gcWindowBase = GC.CollectionCount(0);
        }

        private static ProfilerRecorder Start(ProfilerCategory cat, string stat)
        {
            try { return ProfilerRecorder.StartNew(cat, stat); }
            catch { return default; }
        }

        /// Call once a frame with its time in ms. Returns what this frame's GC did, for hitches.
        public void Frame(float ms, out long gcBytes, out bool gcRan)
        {
            _window.Add(ms);
            if (_session.Count < MaxSessionFrames) _session.Add(ms);
            Ring[RingHead] = ms;
            RingHead = (RingHead + 1) % RingSize;
            if (ms > HitchMs) { _hitches++; SessionHitches++; }

            gcBytes = _gcAlloc.Valid ? _gcAlloc.LastValue : 0;
            _gcAllocWindow += gcBytes;
            int gc = GC.CollectionCount(0);
            gcRan = gc != _gcBase;
            _gcBase = gc;

            if (FrameTimingManager.IsFeatureEnabled())
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _ft) > 0)
                {
                    _cpu += _ft[0].cpuFrameTime;
                    _gpu += _ft[0].gpuFrameTime;
                    _main += _ft[0].cpuMainThreadFrameTime;
                    _render += _ft[0].cpuRenderThreadFrameTime;
                    _timings++;
                }
            }
        }

        /// The window since the last roll-up, as a "perf" event payload (and resets the window).
        public Dictionary<string, object> Rollup()
        {
            var s = PerfStats.Summarise(_window);
            var d = s.ToDict();
            LastFps = s.avgFps;
            LastLow1 = s.low1Fps;
            d["hitches"] = _hitches;
            if (_timings > 0)
            {
                LastCpuMs = (float)(_cpu / _timings);
                LastGpuMs = (float)(_gpu / _timings);
                d["cpu_ms"] = LastCpuMs;
                d["main_ms"] = (float)(_main / _timings);
                d["render_ms"] = (float)(_render / _timings);
                if (_gpu > 0) d["gpu_ms"] = LastGpuMs;
                // which side to optimise. cpu_ms includes waiting for vsync / the frame cap, so
                // compare the GPU against the CPU's actual work (the busier of its two threads)
                float cpuWork = (float)System.Math.Max(_main, _render) / _timings;
                if (_gpu > 0) d["bound"] = LastGpuMs > cpuWork ? "gpu" : "cpu";
                // spare time before the frame gets slower than it is now (positive = headroom)
                d["headroom_ms"] = s.avgMs - Mathf.Max(cpuWork, LastGpuMs);
            }
            long alloc = Profiler.GetTotalAllocatedMemoryLong(), reserved = Profiler.GetTotalReservedMemoryLong();
            long mono = Profiler.GetMonoUsedSizeLong();
            long sys = _sysMem.Valid ? _sysMem.LastValue : 0;
            _peakMem = Math.Max(_peakMem, Math.Max(alloc, sys));
            LastMemMb = alloc / 1048576f;
            d["mem_alloc_mb"] = LastMemMb;
            d["mem_reserved_mb"] = reserved / 1048576f;
            d["mono_mb"] = mono / 1048576f;
            if (sys > 0) d["sys_mem_mb"] = sys / 1048576f;
            int gcNow = GC.CollectionCount(0);
            d["gc_collections"] = gcNow - _gcWindowBase;
            _gcWindowBase = gcNow;
            if (_gcAlloc.Valid && s.frames > 0) d["gc_alloc_kb_per_frame"] = _gcAllocWindow / 1024f / s.frames;
            if (_draws.Valid && _draws.LastValue > 0) d["draw_calls"] = _draws.LastValue;
            if (_batches.Valid && _batches.LastValue > 0) d["batches"] = _batches.LastValue;
            if (_setPass.Valid && _setPass.LastValue > 0) d["setpass"] = _setPass.LastValue;
            if (_tris.Valid && _tris.LastValue > 0) d["tris"] = _tris.LastValue;

            _window.Clear();
            _cpu = _gpu = _main = _render = 0;
            _timings = 0;
            _hitches = 0;
            _gcAllocWindow = 0;
            return d;
        }

        public PerfSummary SessionSummary() => PerfStats.Summarise(_session);

        public void Dispose()
        {
            _gcAlloc.Dispose(); _sysMem.Dispose(); _draws.Dispose();
            _batches.Dispose(); _setPass.Dispose(); _tris.Dispose();
        }
    }
}
