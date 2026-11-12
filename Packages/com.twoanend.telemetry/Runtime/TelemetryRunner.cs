using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TwoAnEnd.Playtest
{
    /// The one object telemetry lives on (DontDestroyOnLoad). Each frame: samples performance,
    /// logs hitches, rolls up perf events, samples tracked positions, flushes the file every
    /// second and ticks the uploader. Logs scene changes and finishes "load" spans on the
    /// first frame of the new scene. Draws the F3 overlay, the consent prompt and the corner
    /// watermark (IMGUI, so it works whatever input system or UI the game uses).
    [AddComponentMenu("")]
    [DefaultExecutionOrder(-10000)]
    public sealed class TelemetryRunner : MonoBehaviour
    {
        public readonly PerfSampler Perf = new PerfSampler();

        private TelemetryConfig Cfg => Telemetry.Config;
        private KeyCode _overlayKey = KeyCode.F3;
        private bool _overlay;
        private float _nextPerf, _nextFlush, _nextPos, _lastHitch, _benchAutoAt;
        private int _suppressedHitches;
        private readonly Dictionary<string, Transform> _tracked = new Dictionary<string, Transform>();
        private readonly Dictionary<string, List<object>> _paths = new Dictionary<string, List<object>>();
        private readonly Queue<string> _feed = new Queue<string>();

        // ── lifecycle ───────────────────────────────────────────────────────
        private void Awake()
        {
            if (!System.Enum.TryParse(Cfg.overlayKey, true, out _overlayKey)) _overlayKey = KeyCode.F3;
            Perf.HitchMs = Cfg.hitchMs;
            Perf.Begin();
            // a benchmark must keep running when its window isn't focused (Unity pauses otherwise)
            if (Benchmark.Requested) Application.runInBackground = true;
            float now = Time.realtimeSinceStartup;
            _nextPerf = now + Cfg.perfInterval;
            _nextFlush = now + 1f;
            _nextPos = now + Cfg.positionInterval;
            // a benchmark the game is taking to a scene waits for its Begin (up to a minute); otherwise it begins here
            _benchAutoAt = Benchmark.Requested ? now + (string.IsNullOrEmpty(Benchmark.Scene) ? 6f : 60f) : float.MaxValue;
            SceneManager.activeSceneChanged += OnSceneChanged;
            Application.quitting += OnQuitting;
            Telemetry.Logged += OnLogged;

            // evidence capture from the command line: open the overlay, and/or save one of the
            // game's own frames (Unity renders it, so another window on top doesn't matter)
            if (Benchmark.HasArg("-telemetryOverlay")) _overlay = true;
            _shotPath = Benchmark.Arg("-telemetryScreenshot");
            _shotAt = float.TryParse(Benchmark.Arg("-telemetryScreenshotAt"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : 10f;
            if (!string.IsNullOrEmpty(_shotPath)) Application.runInBackground = true;
        }

        private string _shotPath;
        private float _shotAt;

        private IEnumerator TakeShot()
        {
            string path = _shotPath;
            _shotPath = null;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            ScreenCapture.CaptureScreenshot(path);
            Telemetry.Log("screenshot", "path", Path.GetFileName(path));
            // the file is written at the end of the frame
            for (int i = 0; i < 10; i++) yield return null;
            if (Benchmark.HasArg("-telemetryQuitAfterShot") && !Application.isEditor) Application.Quit();
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
            Application.quitting -= OnQuitting;
            Telemetry.Logged -= OnLogged;
            Perf.Dispose();
            if (_blocker != null) Destroy(_blocker);
        }

        private void OnQuitting() => Telemetry.EndSession("quit");

        private void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            Telemetry.Log("app_pause");
            Telemetry.Flush();
        }

        private int _skipFrames;

        private void OnApplicationFocus(bool focus)
        {
            if (focus) _skipFrames = 2;
            if (Telemetry.Started) Telemetry.Log("focus", "on", focus);
        }

        // ── per frame ───────────────────────────────────────────────────────
        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            float ms = Time.unscaledDeltaTime * 1000f;
            // the first frames after coming back from the background carry the whole time away
            // (Unity pauses unfocused): that's not a frame anyone played, so it isn't counted
            if (_skipFrames > 0) { _skipFrames--; return; }
            Perf.Frame(ms, out long gcBytes, out bool gcRan);

            // a hitch: what was going on (at most four a second, the rest counted)
            if (ms > Cfg.hitchMs && Time.frameCount > 10)
            {
                if (now - _lastHitch < 0.25f) _suppressedHitches++;
                else
                {
                    _lastHitch = now;
                    var d = new Dictionary<string, object>
                    {
                        { "ms", ms },
                        { "gc_kb", gcBytes / 1024f },
                        { "gc_ran", gcRan },
                        { "loading", Telemetry.IsSpanOpen("load") },
                        { "ctx", Telemetry.SnapshotContext() },
                    };
                    if (_suppressedHitches > 0) { d["suppressed"] = _suppressedHitches; _suppressedHitches = 0; }
                    Telemetry.Log("hitch", d);
                }
            }

            if (now >= _nextPerf)
            {
                _nextPerf = now + Cfg.perfInterval;
                var d = Perf.Rollup();
                d["timescale"] = Time.timeScale;
                Telemetry.Log("perf", d);
            }

            if (now >= _nextPos)
            {
                _nextPos = now + Cfg.positionInterval;
                SamplePositions();
            }

            if (now >= _nextFlush)
            {
                _nextFlush = now + 1f;
                Telemetry.Flush();
            }
            Telemetry.TickUpload(now);

            if (now >= _benchAutoAt && !BenchmarkRunning && !_benchDone) BeginBenchmark(Telemetry.CurrentScene);
            if (BenchmarkRunning) TickBenchmark(now, ms);
            if (_shotPath != null && Time.realtimeSinceStartup >= _shotAt) StartCoroutine(TakeShot());

            if (_overlay) DrawGraph();
        }

        // ── scenes ──────────────────────────────────────────────────────────
        private void OnSceneChanged(Scene from, Scene to)
        {
            FlushPaths();
            string prev = Telemetry.CurrentScene;
            Telemetry.CurrentScene = to.name;
            Telemetry.Log("scene", "from", prev, "to", to.name);
            if (Telemetry.IsSpanOpen("load")) StartCoroutine(EndLoadOnFirstFrame(to.name));
        }

        // a load is done when the new scene has drawn its first frame
        private IEnumerator EndLoadOnFirstFrame(string scene)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            Telemetry.EndSpan("load", "scene", scene);
        }

        // ── positions ───────────────────────────────────────────────────────
        internal void Track(string channel, Transform target)
        {
            if (string.IsNullOrEmpty(channel)) return;
            FlushPath(channel);
            if (target == null) _tracked.Remove(channel);
            else _tracked[channel] = target;
        }

        private readonly List<string> _dead = new List<string>();

        private void SamplePositions()
        {
            if (_tracked.Count == 0) return;
            float t = (float)System.Math.Round(Telemetry.SessionTime, 1);
            _dead.Clear();
            foreach (var kv in _tracked)
            {
                if (kv.Value == null) { _dead.Add(kv.Key); continue; }
                var p = kv.Value.position;
                if (!_paths.TryGetValue(kv.Key, out var list)) _paths[kv.Key] = list = new List<object>();
                list.Add(new[] { t, R(p.x), R(p.y), R(p.z) });
                if (list.Count >= Mathf.Max(1, Cfg.pathBatch)) FlushPath(kv.Key);
            }
            foreach (var k in _dead) { FlushPath(k); _tracked.Remove(k); }
        }

        private static float R(float v) => Mathf.Round(v * 100f) / 100f;

        private void FlushPath(string channel)
        {
            if (!_paths.TryGetValue(channel, out var list) || list.Count == 0) return;
            Telemetry.Log("path", "ch", channel, "pts", list.ToArray());
            list.Clear();
        }

        internal void FlushPaths()
        {
            foreach (var k in new List<string>(_paths.Keys)) FlushPath(k);
        }

        // ── benchmark ───────────────────────────────────────────────────────
        public bool BenchmarkRunning { get; private set; }
        private bool _benchDone;
        private string _benchLabel;
        private float _benchWarm, _benchEnd;
        private readonly List<float> _benchFrames = new List<float>(16384);
        private PerfSampler _benchPerf;

        internal void BeginBenchmark(string label)
        {
            if (BenchmarkRunning || _benchDone) return;
            BenchmarkRunning = true;
            _benchLabel = string.IsNullOrEmpty(label) ? "scene" : label;
            float now = Time.realtimeSinceStartup;
            _benchWarm = now + 2f;
            _benchEnd = _benchWarm + Benchmark.Seconds;
            _benchFrames.Clear();
            _benchPerf = new PerfSampler { HitchMs = Cfg.hitchMs };
            _benchPerf.Begin();
            Telemetry.Log("benchmark_start", "label", _benchLabel, "seconds", Benchmark.Seconds);
        }

        private void TickBenchmark(float now, float ms)
        {
            if (now < _benchWarm) return;
            _benchFrames.Add(ms);
            _benchPerf.Frame(ms, out _, out _);
            if (now < _benchEnd) return;

            BenchmarkRunning = false;
            _benchDone = true;
            var s = PerfStats.Summarise(_benchFrames);
            var report = s.ToDict();
            foreach (var kv in _benchPerf.Rollup()) if (!report.ContainsKey(kv.Key)) report[kv.Key] = kv.Value;
            report["label"] = _benchLabel;
            report["seconds"] = Benchmark.Seconds;
            report["build"] = Telemetry.Build;
            report["session_id"] = Telemetry.SessionId;
            report["machine"] = SessionInfo.Header(Cfg);
            _benchPerf.Dispose();
            Telemetry.Log("benchmark", report);
            string safeBuild = Telemetry.Build.Replace('+', '_');
            string path = Path.Combine(Telemetry.LogDirectory, $"benchmark_{safeBuild}_{_benchLabel}_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
            try
            {
                Directory.CreateDirectory(Telemetry.LogDirectory);
                File.WriteAllText(path, Json.Write(report));
                Debug.Log($"[Telemetry] benchmark done: {s.avgFps:0.0} fps avg, {s.low1Fps:0.0} 1% low -> {path}");
            }
            catch (IOException e) { Debug.LogWarning($"[Telemetry] couldn't write the benchmark: {e.Message}"); }
            Benchmark.RaiseFinished(path);
            if (Benchmark.QuitWhenDone && !Application.isEditor) Application.Quit();
        }

        // ── the live feed ───────────────────────────────────────────────────
        private void OnLogged(TelemetryEvent e)
        {
            if (e.type == "path") return;
            // one short line each: "12.4  judge  lane=2 result=good ms=-18.4"
            var sb = new System.Text.StringBuilder();
            foreach (var kv in e.data)
            {
                if (kv.Value == null || kv.Value is System.Collections.IDictionary || (kv.Value is System.Collections.IEnumerable && !(kv.Value is string))) continue;
                string v = kv.Value is float f ? f.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                         : kv.Value is double dd ? dd.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                         : System.Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (v.Length > 24) v = v.Substring(0, 24) + "…";
                sb.Append(kv.Key).Append('=').Append(v).Append("  ");
                if (sb.Length > 46) break;
            }
            string data = sb.Length > 50 ? sb.ToString(0, 50) + "…" : sb.ToString();
            _feed.Enqueue($"<color=#8fd3ff>{e.t,6:0.0}</color>  <b>{e.type}</b>  <color=#c9d2e3>{data}</color>");
            while (_feed.Count > 10) _feed.Dequeue();
        }

        // ── consent prompt (and a raycast blocker so clicks don't fall through to the game) ──
        private bool _prompt;
        private GameObject _blocker;
        private bool _promptDetails;

        public bool ShowConsentPrompt
        {
            get => _prompt;
            set
            {
                if (_prompt == value) return;
                _prompt = value;
                SetBlocker(value);
                Telemetry.NotifyPrompt(value);
            }
        }

        private void SetBlocker(bool on)
        {
            if (on && _blocker == null)
            {
                _blocker = new GameObject("[Telemetry] Blocker");
                DontDestroyOnLoad(_blocker);
                var c = _blocker.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = short.MaxValue;
                _blocker.AddComponent<GraphicRaycaster>();
                var img = new GameObject("Block").AddComponent<Image>();
                img.transform.SetParent(_blocker.transform, false);
                img.color = new Color(0f, 0f, 0f, 0f);
                var rt = img.rectTransform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
            }
            if (_blocker != null) _blocker.SetActive(on);
        }

        // ── IMGUI ───────────────────────────────────────────────────────────
        private Texture2D _graph, _px;
        private Color32[] _graphPx;
        private const int GraphW = PerfSampler.RingSize, GraphH = 90;
        private GUIStyle _panel, _text, _small, _title, _button, _buttonAlt, _mark;

        private void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == _overlayKey && Telemetry.Started)
            {
                _overlay = !_overlay;
                Telemetry.Log("overlay", "on", _overlay);
                e.Use();
            }
            EnsureStyles();
            float scale = Mathf.Max(0.75f, Screen.height / 1080f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale, h = Screen.height / scale;
            GUI.depth = -10000;

            if (Cfg.watermark && Telemetry.Recording && !_overlay)
                GUI.Label(new Rect(10, h - 26, 600, 20), $"PLAYTEST  ·  {Telemetry.SessionCode}  ·  {Telemetry.Build}  ·  {Cfg.overlayKey} stats", _mark);
            if (_overlay) DrawOverlay(w, h);
            if (_prompt) DrawPrompt(w, h);
            GUI.matrix = old;
        }

        private void DrawOverlay(float w, float h)
        {
            const float pw = 440f;
            var r = new Rect(w - pw - 14, 14, pw, 470);
            GUI.Box(r, GUIContent.none, _panel);
            float x = r.x + 14, y = r.y + 10;
            GUI.Label(new Rect(x, y, pw - 28, 24), "TELEMETRY", _title);
            string state = Telemetry.Recording ? "<color=#7dffa8>● REC</color>" : Telemetry.Consent == ConsentState.Denied ? "<color=#ff8a8a>■ OFF (no consent)</color>" : "<color=#ffd27d>● WAITING FOR CONSENT</color>";
            GUI.Label(new Rect(x + 140, y + 4, pw - 168, 20), state, _small);
            y += 28;
            GUI.Label(new Rect(x, y, pw - 28, 20), $"session <b>{Telemetry.SessionCode}</b>   build {Telemetry.Build}", _small);
            y += 22;

            // frame-time graph (16.7 / 33.3 ms guides)
            if (_graph != null) GUI.DrawTexture(new Rect(x, y, pw - 28, GraphH), _graph);
            GUI.Label(new Rect(x + 4, y + 2, 200, 16), "frame time (0–50 ms)", _small);
            y += GraphH + 6;

            var p = Perf;
            string gpu = p.LastGpuMs > 0f ? $"{p.LastGpuMs:0.0} ms" : "n/a";
            GUI.Label(new Rect(x, y, pw - 28, 60),
                $"<b>{p.LastFps:0} fps</b>   1% low {p.LastLow1:0}   cpu {p.LastCpuMs:0.0} ms   gpu {gpu}\n" +
                $"mem {p.LastMemMb:0} MB   hitches {p.SessionHitches}   events {Telemetry.RecordedCount}   scene {Telemetry.CurrentScene}", _text);
            y += 42;
            var up = Telemetry.Upload;
            string upl = up == null ? (Cfg.SupabaseConfigured ? "upload off (editor)" : "local only") : $"supabase: {up.Status}   ·   waiting {up.Queued}";
            if (up != null && !string.IsNullOrEmpty(up.LastError) && up.Failures > 0) upl += $"\n<color=#ff9a9a>{up.LastError}</color>";
            GUI.Label(new Rect(x, y, pw - 28, 36), upl, _small);
            y += 38;

            GUI.Label(new Rect(x, y, pw - 28, 18), "LIVE EVENTS", _small);
            y += 18;
            GUI.Label(new Rect(x, y, pw - 28, 200), string.Join("\n", _feed), _small);

            // consent controls
            float by = r.yMax - 36;
            if (Telemetry.Recording)
            {
                if (GUI.Button(new Rect(x, by, 160, 26), "Withdraw consent", _buttonAlt)) Telemetry.SetConsent(false, "overlay");
            }
            else if (GUI.Button(new Rect(x, by, 160, 26), "Give consent", _button)) ShowConsentPrompt = true;
            if (GUI.Button(new Rect(x + 170, by, 120, 26), "Open logs", _buttonAlt)) Application.OpenURL("file://" + Telemetry.LogDirectory);
        }

        private void DrawPrompt(float w, float h)
        {
            GUI.DrawTexture(new Rect(0, 0, w, h), Px(new Color(0.02f, 0.03f, 0.08f, 0.72f)));
            float pw = 640f, ph = _promptDetails ? 560f : 400f;
            var r = new Rect((w - pw) * 0.5f, (h - ph) * 0.5f, pw, ph);
            GUI.Box(r, GUIContent.none, _panel);
            float x = r.x + 30, y = r.y + 24, cw = pw - 60;
            GUI.Label(new Rect(x, y, cw, 30), $"{Cfg.studyName.ToUpperInvariant()}: HELP US TEST", _title);
            y += 40;
            GUI.Label(new Rect(x, y, cw, 130),
                "While you play, this build can record <b>anonymous</b> gameplay and performance data to help us find bugs and balance the game. " +
                "No names, accounts, device names or keystrokes outside the game are recorded.\n\n" +
                $"Your session code is <b><size=20>{Telemetry.SessionCode}</size></b>. Put it in the feedback survey so your answers can be matched to your play.", _text);
            y += 140;
            if (GUI.Button(new Rect(x, y, 200, 24), _promptDetails ? "▾ hide what's recorded" : "▸ what's recorded?", _small)) _promptDetails = !_promptDetails;
            y += 28;
            if (_promptDetails)
            {
                GUI.Label(new Rect(x, y, cw, 160),
                    "• your machine: OS, CPU, GPU, RAM, screen, quality settings\n" +
                    "• performance: frame rate, frame times, stutters, memory, load times\n" +
                    "• gameplay: notes hit / missed and how early or late, battle results, retries\n" +
                    "• where you walk, who you talk to, how far through the story you get\n" +
                    "• the game build and a random id for this install\n" +
                    "Saved on this PC" + (Cfg.SupabaseConfigured ? " and uploaded to the study's database" : "") +
                    ". You can withdraw at any time (F3), which deletes what's on this PC.", _small);
                y += 160;
            }
            float by = r.yMax - 64;
            if (GUI.Button(new Rect(x, by, 300, 40), "I agree, record my session", _button)) Telemetry.SetConsent(true, "prompt");
            if (GUI.Button(new Rect(x + 316, by, cw - 316, 40), "No thanks", _buttonAlt)) Telemetry.SetConsent(false, "prompt");
        }

        private void DrawGraph()
        {
            if (_graph == null)
            {
                _graph = new Texture2D(GraphW, GraphH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _graphPx = new Color32[GraphW * GraphH];
            }
            var bg = new Color32(10, 14, 28, 200);
            for (int i = 0; i < _graphPx.Length; i++) _graphPx[i] = bg;
            int g60 = Mathf.RoundToInt(16.7f / 50f * GraphH), g30 = Mathf.RoundToInt(33.3f / 50f * GraphH);
            for (int x = 0; x < GraphW; x++)
            {
                _graphPx[g60 * GraphW + x] = new Color32(120, 255, 170, 90);
                _graphPx[g30 * GraphW + x] = new Color32(255, 210, 120, 90);
                float ms = Perf.Ring[(Perf.RingHead + x) % GraphW];
                int bh = Mathf.Clamp(Mathf.RoundToInt(ms / 50f * GraphH), 0, GraphH);
                var c = ms <= 17.5f ? new Color32(110, 230, 255, 255) : ms <= 34f ? new Color32(255, 210, 110, 255) : new Color32(255, 100, 120, 255);
                for (int y = 0; y < bh; y++) _graphPx[y * GraphW + x] = c;
            }
            _graph.SetPixels32(_graphPx);
            _graph.Apply(false);
        }

        private Texture2D Px(Color c)
        {
            if (_px == null) _px = new Texture2D(1, 1);
            _px.SetPixel(0, 0, c);
            _px.Apply();
            return _px;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void EnsureStyles()
        {
            if (_panel != null) return;
            _panel = new GUIStyle(GUI.skin.box) { normal = { background = Solid(new Color(0.05f, 0.07f, 0.14f, 0.93f)) }, border = new RectOffset(0, 0, 0, 0) };
            _text = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, richText = true, normal = { textColor = new Color(0.92f, 0.95f, 1f) } };
            _small = new GUIStyle(_text) { fontSize = 12, normal = { textColor = new Color(0.78f, 0.84f, 0.95f) } };
            _title = new GUIStyle(_text) { fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.55f, 0.85f, 1f) } };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { background = Solid(new Color(1f, 0.3f, 0.62f)), textColor = Color.white }, hover = { background = Solid(new Color(1f, 0.45f, 0.72f)), textColor = Color.white }, active = { background = Solid(new Color(0.85f, 0.2f, 0.5f)), textColor = Color.white } };
            _buttonAlt = new GUIStyle(_button) { normal = { background = Solid(new Color(0.2f, 0.25f, 0.38f)), textColor = Color.white }, hover = { background = Solid(new Color(0.28f, 0.34f, 0.5f)), textColor = Color.white }, active = { background = Solid(new Color(0.15f, 0.18f, 0.28f)), textColor = Color.white } };
            _mark = new GUIStyle(_small) { fontSize = 11, normal = { textColor = new Color(1f, 1f, 1f, 0.55f) } };
        }
    }
}
