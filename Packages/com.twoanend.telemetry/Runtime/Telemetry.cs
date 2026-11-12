using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwoAnEnd.Playtest
{
    public enum ConsentState { Unknown = 0, Granted = 1, Denied = 2 }

    /// One thing that happened, as logged.
    public sealed class TelemetryEvent
    {
        public string type, scene;
        public long seq;
        /// seconds since the session started (real time, ignores pauses and time scale)
        public double t;
        public DateTime utc;
        public IDictionary<string, object> data;
        /// the JSON line exactly as written: its keys are the Supabase table's columns
        public string line;
    }

    /// The whole API. Starts itself when the game boots (unless the config says not to),
    /// asks for consent, and from then on anything the game logs goes to a JSONL file and,
    /// if a Supabase project is configured, up to it.
    ///
    /// Consent gates everything. Before it's given, events wait in memory (so the session
    /// header and the first frames aren't lost); given, they're written; refused, they're
    /// dropped, and taking it back deletes what's on this machine.
    ///
    ///   Telemetry.Log("judge", "lane", 2, "result", "good", "ms", -18.4f);
    ///   Telemetry.BeginSpan("load", "to", "Overworld");  ...  Telemetry.EndSpan("load");
    ///   Telemetry.Track("overworld", playerTransform);
    ///   Telemetry.AddContext("song", () => currentSongId);   // attached to hitches
    public static class Telemetry
    {
        public const string Version = "1.0.0";
        private const string ConsentKey = "twoanend.telemetry.consent";
        private const int MaxPending = 5000;

        public static TelemetryConfig Config { get; private set; } = new TelemetryConfig();
        public static bool Started { get; private set; }
        public static string SessionId { get; private set; } = "";
        /// short code shown to the tester, typed into the survey to link their answers
        public static string SessionCode { get; private set; } = "";
        public static string PlayerId { get; private set; } = "";
        public static string Build { get; private set; } = "";
        public static ConsentState Consent { get; private set; }
        /// events are being written (started, enabled and consented)
        public static bool Recording => Started && Config.enabled && Consent == ConsentState.Granted;
        public static double SessionTime => Started ? Time.realtimeSinceStartupAsDouble - _t0 : 0;
        public static string CurrentScene { get; internal set; } = "";
        public static long EventCount => _seq;
        public static long RecordedCount { get; private set; }
        public static IReadOnlyDictionary<string, int> CountsByType => _counts;
        public static string LogDirectory => Path.Combine(Application.persistentDataPath, "Telemetry");
        public static JsonlSink LocalFile => _file;
        public static SupabaseSink Upload => _upload;
        /// the built-in consent prompt is on screen (the game may want to hold its input)
        public static bool ConsentPromptOpen => _runner != null && _runner.ShowConsentPrompt;

        /// every event, recorded or not (the overlay's live feed)
        public static event Action<TelemetryEvent> Logged;
        public static event Action<ConsentState> ConsentChanged;
        public static event Action<bool> ConsentPromptChanged;

        private static long _seq;
        private static double _t0;
        private static string _fileStem = "";
        private static bool _ended;
        private static readonly List<TelemetryEvent> _pending = new List<TelemetryEvent>();
        private static readonly Dictionary<string, Func<object>> _context = new Dictionary<string, Func<object>>();
        private static readonly Dictionary<string, (double t, Dictionary<string, object> data)> _spans = new Dictionary<string, (double, Dictionary<string, object>)>();
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private static JsonlSink _file;
        private static SupabaseSink _upload;
        private static TelemetryRunner _runner;
        internal static TelemetryRunner Runner => _runner;

        // statics survive play mode when domain reload is off: start clean every time
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Started = false; _ended = false; _seq = 0; RecordedCount = 0;
            _pending.Clear(); _context.Clear(); _spans.Clear(); _counts.Clear();
            _file = null; _upload = null; _runner = null;
            Logged = null; ConsentChanged = null; ConsentPromptChanged = null;
            Consent = ConsentState.Unknown;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoStart()
        {
            var cfg = TelemetryConfig.Load();
            if (cfg.autoStart || Benchmark.Requested) Start(cfg);
        }

        public static void Start(TelemetryConfig cfg = null)
        {
            if (Started) return;
            Config = cfg ?? TelemetryConfig.Load();
            if (!Config.enabled) return;
            Started = true;
            _ended = false;
            _t0 = Time.realtimeSinceStartupAsDouble;
            SessionId = SessionInfo.NewSessionId();
            SessionCode = SessionInfo.CodeFor(SessionId);
            PlayerId = SessionInfo.PlayerId();
            Build = SessionInfo.BuildTag();
            CurrentScene = SceneManager.GetActiveScene().name;
            _fileStem = $"{DateTime.Now:yyyyMMdd_HHmmss}_{SessionCode}";

            var go = new GameObject("[Telemetry]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<TelemetryRunner>();

            Log("session_start", SessionInfo.Header(Config));

            // a benchmark run, or the developer in the editor, counts as consented; otherwise a
            // remembered answer, or ask
            if (Benchmark.Requested || (Application.isEditor && Config.editorAutoConsent))
                SetConsentInternal(true, Benchmark.Requested ? "benchmark" : "editor", false);
            else if (!Config.askEverySession && PlayerPrefs.HasKey(ConsentKey))
                SetConsentInternal(PlayerPrefs.GetInt(ConsentKey) == (int)ConsentState.Granted, "remembered", false);
            else if (Config.askConsent)
                _runner.ShowConsentPrompt = true;
        }

        // ── consent ─────────────────────────────────────────────────────────
        /// The tester's answer. `source` says where it came from ("prompt", "survey", ...).
        /// Taking consent back deletes this machine's telemetry files and stops uploads.
        public static void SetConsent(bool granted, string source = "game") => SetConsentInternal(granted, source, true);

        private static void SetConsentInternal(bool granted, string source, bool remember)
        {
            if (!Started) Start();
            if (!Started) return;
            var was = Consent;
            Consent = granted ? ConsentState.Granted : ConsentState.Denied;
            if (remember && !Config.askEverySession)
            {
                PlayerPrefs.SetInt(ConsentKey, (int)Consent);
                PlayerPrefs.Save();
            }
            if (granted)
            {
                OpenSinks();
                Log("consent", "granted", true, "source", source);
            }
            else
            {
                _pending.Clear();
                CloseSinks();
                if (was == ConsentState.Granted) DeleteLocalData();
            }
            if (_runner != null) _runner.ShowConsentPrompt = false;
            if (was != Consent) ConsentChanged?.Invoke(Consent);
        }

        internal static void NotifyPrompt(bool open) => ConsentPromptChanged?.Invoke(open);

        private static void OpenSinks()
        {
            if (_file == null)
            {
                try { _file = new JsonlSink(LogDirectory, _fileStem + ".jsonl"); }
                catch (Exception e) { Debug.LogWarning($"[Telemetry] can't write to {LogDirectory}: {e.Message}"); }
            }
            if (_upload == null && Config.SupabaseConfigured && (!Application.isEditor || Config.uploadFromEditor))
                _upload = new SupabaseSink(Config, LogDirectory);
            // what happened before consent, in order
            foreach (var e in _pending) Record(e);
            _pending.Clear();
        }

        private static void CloseSinks()
        {
            _file?.Dispose();
            _file = null;
            _upload?.Dispose();
            _upload = null;
        }

        /// Deletes every telemetry file on this machine (logs, summaries, the upload outbox).
        public static void DeleteLocalData()
        {
            bool reopen = _file != null;
            _file?.Dispose();
            _file = null;
            try
            {
                if (Directory.Exists(LogDirectory))
                    foreach (var f in Directory.GetFiles(LogDirectory)) File.Delete(f);
            }
            catch (IOException e) { Debug.LogWarning($"[Telemetry] couldn't delete everything: {e.Message}"); }
            if (reopen && Recording) _file = new JsonlSink(LogDirectory, _fileStem + ".jsonl");
        }

        // ── logging ─────────────────────────────────────────────────────────
        /// Logs an event: a type and "key", value pairs (or one dictionary).
        public static void Log(string type, params object[] keyValues) => Log(type, Json.Pairs(keyValues));

        public static void Log(string type, IDictionary<string, object> data)
        {
            if (!Started || _ended || string.IsNullOrEmpty(type) || Consent == ConsentState.Denied && Logged == null) return;
            var e = new TelemetryEvent
            {
                type = type,
                seq = ++_seq,
                t = SessionTime,
                utc = DateTime.UtcNow,
                scene = CurrentScene,
                data = data ?? new Dictionary<string, object>(),
            };
            var sb = new StringBuilder(160);
            sb.Append("{\"session_id\":"); Json.Str(sb, SessionId);
            sb.Append(",\"seq\":").Append(e.seq.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"type\":"); Json.Str(sb, type);
            sb.Append(",\"t\":"); Json.Write(sb, Math.Round(e.t, 4));
            sb.Append(",\"utc\":"); Json.Str(sb, e.utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            sb.Append(",\"scene\":"); Json.Write(sb, string.IsNullOrEmpty(e.scene) ? null : e.scene);
            sb.Append(",\"build\":"); Json.Str(sb, Build);
            sb.Append(",\"data\":"); Json.Obj(sb, e.data);
            sb.Append('}');
            e.line = sb.ToString();

            try { Logged?.Invoke(e); }
            catch (Exception ex) { Debug.LogException(ex); }

            if (Consent == ConsentState.Granted) Record(e);
            else if (Consent == ConsentState.Unknown && _pending.Count < MaxPending) _pending.Add(e);
        }

        private static void Record(TelemetryEvent e)
        {
            _file?.Write(e.line);
            _upload?.Enqueue(e.line);
            RecordedCount++;
            _counts.TryGetValue(e.type, out int c);
            _counts[e.type] = c + 1;
        }

        // ── context, spans, tracking ────────────────────────────────────────
        /// Something worth knowing when a hitch happens ("song", "song_t", "battle"...),
        /// read at that moment.
        public static void AddContext(string key, Func<object> getter) { if (!string.IsNullOrEmpty(key) && getter != null) _context[key] = getter; }
        public static void RemoveContext(string key) { if (key != null) _context.Remove(key); }

        public static Dictionary<string, object> SnapshotContext()
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in _context)
            {
                try { d[kv.Key] = kv.Value(); }
                catch { d[kv.Key] = null; }
            }
            return d;
        }

        /// Starts timing something (a load, a level, a menu). EndSpan logs a "span" event
        /// with its name and how long it took in ms.
        public static void BeginSpan(string name, params object[] keyValues)
        {
            if (!Started || string.IsNullOrEmpty(name)) return;
            _spans[name] = (SessionTime, Json.Pairs(keyValues));
        }

        public static bool IsSpanOpen(string name) => name != null && _spans.ContainsKey(name);

        public static void EndSpan(string name, params object[] keyValues)
        {
            if (name == null || !_spans.TryGetValue(name, out var s)) return;
            _spans.Remove(name);
            var d = new Dictionary<string, object> { { "name", name }, { "ms", (float)((SessionTime - s.t) * 1000.0) } };
            foreach (var kv in s.data) d[kv.Key] = kv.Value;
            foreach (var kv in Json.Pairs(keyValues)) d[kv.Key] = kv.Value;
            Log("span", d);
        }

        /// Samples a transform's position every positionInterval seconds into "path" events
        /// (for heatmaps of where players go). Same channel again replaces it; null stops.
        public static void Track(string channel, Transform target) => _runner?.Track(channel, target);
        public static void Untrack(string channel) => _runner?.Track(channel, null);

        /// Writes buffered lines to disk and saves the upload outbox.
        public static void Flush()
        {
            _file?.Flush();
            _upload?.Persist();
        }

        internal static void TickUpload(float now) => _upload?.Tick(now, Config.uploadInterval);

        /// Closes the session: logs "session_end" with the session's totals, writes a summary
        /// file next to the log, flushes. Called when the game quits.
        public static void EndSession(string reason = "quit")
        {
            if (!Started || _ended) return;
            var perf = _runner != null ? _runner.Perf.SessionSummary() : default;
            var summary = new Dictionary<string, object>
            {
                { "reason", reason },
                { "duration_s", (float)SessionTime },
                { "events", RecordedCount + 1 },
                { "perf", perf.ToDict() },
                { "hitches", _runner != null ? _runner.Perf.SessionHitches : 0 },
                { "peak_mem_mb", _runner != null ? _runner.Perf.PeakMemBytes / 1048576f : 0f },
                { "counts", new Dictionary<string, object>(CountsAsObjects()) },
            };
            _runner?.FlushPaths();
            Log("session_end", summary);
            _ended = true;
            if (Recording)
            {
                try
                {
                    summary["session_id"] = SessionId;
                    summary["session_code"] = SessionCode;
                    summary["build"] = Build;
                    summary["player_id"] = PlayerId;
                    File.WriteAllText(Path.Combine(LogDirectory, _fileStem + ".summary.json"), Json.Write(summary));
                }
                catch (IOException) { }
            }
            Flush();
            CloseSinks();
        }

        private static IEnumerable<KeyValuePair<string, object>> CountsAsObjects()
        {
            foreach (var kv in _counts) yield return new KeyValuePair<string, object>(kv.Key, kv.Value);
        }
    }
}
