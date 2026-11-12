using System;
using System.Globalization;

namespace TwoAnEnd.Playtest
{
    /// A repeatable performance test from the command line:
    ///
    ///   Game.exe -benchmark [-benchmarkSeconds 30] [-benchmarkScene Overworld] [-benchmarkNoQuit]
    ///
    /// Telemetry starts consented, the game (through its bridge) goes to the scene asked for
    /// and calls Benchmark.Begin once it's settled; if it never does, it begins on its own
    /// after a few seconds wherever it is. After a 2 s warm-up it records for the set time,
    /// writes benchmark_<build>_<label>_<time>.json next to the logs (plus a "benchmark"
    /// event) and quits, so two builds can be compared like for like.
    public static class Benchmark
    {
        public static bool Requested => HasArg("-benchmark");
        public static float Seconds => float.TryParse(Arg("-benchmarkSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0f ? s : 30f;
        public static string Scene => Arg("-benchmarkScene");
        public static bool QuitWhenDone => !HasArg("-benchmarkNoQuit");
        public static bool Running => Telemetry.Runner != null && Telemetry.Runner.BenchmarkRunning;
        /// fired with the report's path when a run finishes
        public static event Action<string> Finished;

        /// Starts the measured run now (after the warm-up). `label` names it in the report.
        public static void Begin(string label = null) => Telemetry.Runner?.BeginBenchmark(string.IsNullOrEmpty(label) ? Telemetry.CurrentScene : label);

        internal static void RaiseFinished(string path) => Finished?.Invoke(path);

        public static bool HasArg(string name)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
