using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace TwoAnEnd.Playtest
{
    /// A stand-alone test bench for the package, so it can be tried without the game: a tiny
    /// four-lane rhythm toy (D F J K) that logs judgements with their timing error the same way
    /// 2ANEND does, a dot to walk around with the arrow keys (tracked for the heatmap), and
    /// buttons that cause a hitch, a GC spike, a fake load and a story flag. Press F3 to watch
    /// it all arrive. Tools > 2ANEND Telemetry > Create Demo Scene makes a scene with it in.
    [AddComponentMenu("2ANEND/Telemetry Demo")]
    public sealed class TelemetryDemo : MonoBehaviour
    {
        private const float Bpm = 120f, Travel = 1.6f, Perfect = 0.05f, Good = 0.12f;
        private static readonly KeyCode[] Keys = { KeyCode.D, KeyCode.F, KeyCode.J, KeyCode.K };
        private static readonly Color[] LaneCol = { new Color(0.15f, 0.55f, 1f), new Color(0.4f, 0.82f, 1f), new Color(1f, 0.88f, 0.22f), new Color(1f, 0.55f, 0.1f) };

        private class DemoNote { public int lane; public float time; public bool done; }

        private readonly List<DemoNote> _notes = new List<DemoNote>();
        private float _songT, _nextBeat;
        private int _combo, _maxCombo, _perfect, _good, _miss, _flags;
        private string _lastJudge = "";
        private float _lastJudgeAt = -9f;
        private Transform _walker;
        private readonly System.Random _rng = new System.Random(7);
        private readonly List<byte[]> _garbage = new List<byte[]>();
        private Texture2D _px;
        // keys read from IMGUI events, so the demo runs under either input system
        private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();

        private void Start()
        {
            _walker = new GameObject("DemoWalker").transform;
            _walker.SetParent(transform, false);
            Telemetry.Track("demo_field", _walker);
            Telemetry.AddContext("song", () => "demo");
            Telemetry.AddContext("song_t", () => Mathf.Round(_songT * 100f) / 100f);
            Telemetry.Log("battle_start", "song", "demo", "chart", "demo", "lanes", 4, "arcade", true);
        }

        private void OnDestroy()
        {
            Telemetry.Untrack("demo_field");
            Telemetry.RemoveContext("song");
            Telemetry.RemoveContext("song_t");
        }

        private void Update()
        {
            _songT += Time.deltaTime;
            // a note on every beat, sometimes two
            float beat = 60f / Bpm;
            while (_nextBeat < _songT + Travel)
            {
                _notes.Add(new DemoNote { lane = _rng.Next(4), time = _nextBeat + Travel });
                if (_rng.NextDouble() < 0.2) _notes.Add(new DemoNote { lane = _rng.Next(4), time = _nextBeat + Travel + beat * 0.5f });
                _nextBeat += beat;
            }
            foreach (var n in _notes)
                if (!n.done && _songT - n.time > Good)
                {
                    n.done = true;
                    Judge(n.lane, "miss", float.NaN, n.time);
                }
            _notes.RemoveAll(n => n.done && _songT - n.time > 1f);

            // the walker
            var mv = Vector3.zero;
            if (_held.Contains(KeyCode.LeftArrow)) mv.x -= 1f;
            if (_held.Contains(KeyCode.RightArrow)) mv.x += 1f;
            if (_held.Contains(KeyCode.UpArrow)) mv.z += 1f;
            if (_held.Contains(KeyCode.DownArrow)) mv.z -= 1f;
            var p = _walker.localPosition + mv * (6f * Time.deltaTime);
            _walker.localPosition = new Vector3(Mathf.Clamp(p.x, -10f, 10f), 0f, Mathf.Clamp(p.z, -6f, 6f));
        }

        private void Press(int lane)
        {
            DemoNote best = null;
            foreach (var n in _notes)
                if (!n.done && n.lane == lane && Mathf.Abs(_songT - n.time) <= Good && (best == null || Mathf.Abs(_songT - n.time) < Mathf.Abs(_songT - best.time)))
                    best = n;
            if (best == null) return;
            best.done = true;
            float delta = _songT - best.time;
            Judge(lane, Mathf.Abs(delta) <= Perfect ? "perfect" : "good", delta, best.time);
        }

        // the same event 2ANEND's bridge logs
        private void Judge(int lane, string result, float delta, float noteT)
        {
            if (result == "miss") { _miss++; _combo = 0; }
            else { if (result == "perfect") _perfect++; else _good++; _maxCombo = Mathf.Max(_maxCombo, ++_combo); }
            _lastJudge = float.IsNaN(delta) ? "MISS" : $"{result.ToUpperInvariant()}  {delta * 1000f:+0;-0} ms";
            _lastJudgeAt = Time.time;
            Telemetry.Log("judge", "song", "demo", "chart", "demo", "lane", lane, "note", "tap", "result", result,
                "ms", float.IsNaN(delta) ? (object)null : delta * 1000f, "note_t", noteT, "song_t", _songT, "combo", _combo);
        }

        private IEnumerator FakeLoad()
        {
            Telemetry.BeginSpan("load", "to", "DemoRoom");
            yield return new WaitForSecondsRealtime(0.1f);
            System.Threading.Thread.Sleep(450);
            yield return null;
            Telemetry.EndSpan("load", "scene", "DemoRoom");
        }

        private void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                // IMGUI repeats KeyDown while held: only the first one is a press
                if (_held.Add(e.keyCode))
                    for (int i = 0; i < Keys.Length; i++)
                        if (e.keyCode == Keys[i]) { Press(i); e.Use(); }
            }
            else if (e.type == EventType.KeyUp) _held.Remove(e.keyCode);

            if (_px == null) { _px = new Texture2D(1, 1); _px.SetPixel(0, 0, Color.white); _px.Apply(); }
            float sw = Screen.width, sh = Screen.height;
            GUI.color = new Color(0.04f, 0.05f, 0.1f);
            GUI.DrawTexture(new Rect(0, 0, sw, sh), _px);

            // the highway
            float lw = 90f, x0 = sw * 0.3f - lw * 2f, hitY = sh * 0.82f;
            for (int i = 0; i < 4; i++)
            {
                GUI.color = new Color(LaneCol[i].r, LaneCol[i].g, LaneCol[i].b, 0.12f);
                GUI.DrawTexture(new Rect(x0 + i * lw + 4, 0, lw - 8, sh), _px);
                GUI.color = LaneCol[i];
                GUI.DrawTexture(new Rect(x0 + i * lw + 4, hitY - 3, lw - 8, 6), _px);
                GUI.Label(new Rect(x0 + i * lw, hitY + 10, lw, 24), $"  {Keys[i]}");
            }
            foreach (var n in _notes)
            {
                if (n.done) continue;
                float k = 1f - (n.time - _songT) / Travel;
                GUI.color = LaneCol[n.lane];
                GUI.DrawTexture(new Rect(x0 + n.lane * lw + 10, hitY * k - 10, lw - 20, 20), _px);
            }
            GUI.color = Color.white;
            if (Time.time - _lastJudgeAt < 0.6f) GUI.Label(new Rect(x0, hitY - 70, lw * 4, 30), $"<size=22><b>{_lastJudge}</b></size>");
            GUI.Label(new Rect(x0, 20, 400, 60), $"<size=16>combo {_combo}  (best {_maxCombo})\nperfect {_perfect}  good {_good}  miss {_miss}</size>");

            // the field the walker moves in (arrow keys)
            var field = new Rect(sw * 0.58f, sh * 0.2f, sw * 0.36f, sh * 0.36f);
            GUI.color = new Color(1f, 1f, 1f, 0.06f);
            GUI.DrawTexture(field, _px);
            var wp = _walker != null ? _walker.localPosition : Vector3.zero;
            GUI.color = new Color(1f, 0.3f, 0.62f);
            GUI.DrawTexture(new Rect(field.x + (wp.x + 10f) / 20f * field.width - 8, field.y + (1f - (wp.z + 6f) / 12f) * field.height - 8, 16, 16), _px);
            GUI.color = Color.white;
            GUI.Label(new Rect(field.x, field.y - 24, field.width, 22), "arrow keys: walk (tracked for the heatmap)");

            // things to break
            float bx = field.x, by = field.yMax + 20;
            if (GUI.Button(new Rect(bx, by, 200, 32), "Hitch (150 ms)")) System.Threading.Thread.Sleep(150);
            if (GUI.Button(new Rect(bx + 210, by, 200, 32), "GC spike (64 MB)")) { for (int i = 0; i < 64; i++) _garbage.Add(new byte[1 << 20]); _garbage.Clear(); System.GC.Collect(); }
            if (GUI.Button(new Rect(bx, by + 40, 200, 32), "Fake scene load")) StartCoroutine(FakeLoad());
            if (GUI.Button(new Rect(bx + 210, by + 40, 200, 32), "Reach a story flag")) Telemetry.Log("flag", "flag", $"demo_chapter_{++_flags}", "on", true);
            if (GUI.Button(new Rect(bx, by + 80, 200, 32), "End battle (win)"))
                Telemetry.Log("battle_end", "song", "demo", "chart", "demo", "outcome", "Win", "score", _perfect * 300 + _good * 100, "max_combo", _maxCombo,
                    "perfect", _perfect, "good", _good, "miss", _miss, "accuracy", 100f * (_perfect + _good * 0.5f) / Mathf.Max(1, _perfect + _good + _miss));
            if (GUI.Button(new Rect(bx + 210, by + 80, 200, 32), "Survey answer")) Telemetry.Log("survey", "question", "difficulty", "answer", _rng.Next(1, 6));
            GUI.Label(new Rect(bx, by + 124, 420, 40), $"F3: live telemetry overlay    session {Telemetry.SessionCode}");
        }
    }
}
