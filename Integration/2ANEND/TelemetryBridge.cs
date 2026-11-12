using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using TwoAnEnd.Autoload;
using TwoAnEnd.Data;
using TwoAnEnd.Gameplay;
using TwoAnEnd.Overworld;
using TwoAnEnd.Playtest;
using TwoAnEnd.Spire;
using TwoAnEnd.UI;

namespace TwoAnEnd.Integration
{
    // 2ANEND -> the telemetry package (Packages/com.twoanend.telemetry). The package knows
    // nothing about the game; this listens to the game's events and logs them:
    //
    //   battles   battle_start / battle_end (score, accuracy, grade, outcome, where you died,
    //             how many attempts), judge (every note: lane, type, result, timing error in ms),
    //             hold (release timing), battle_pause / resume / forfeit / loop / restart
    //   world     path (where Cheri walks, in the city and the club), talk_start / talk_end
    //             (who, for how long), objective (each new one and how long the last took),
    //             flag (story progress), options (audio offset, note speed...)
    //   loads     a "load" span from a scene change starting to its first frame
    //   hitches   tagged with the song and song time when they happen in a battle
    //
    // It also holds the game still while the consent prompt is up, and drives -benchmark
    // (-benchmarkScene Overworld goes there first).
    public sealed class TelemetryBridge : MonoBehaviour
    {
        private CombatController _battle;
        private float _battleStart;
        private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>();
        private string _talkWith;
        private double _talkAt, _objectiveAt;
        private bool _holding;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Telemetry.Started) return;
            var go = new GameObject("[TelemetryBridge]");
            DontDestroyOnLoad(go);
            go.AddComponent<TelemetryBridge>();
        }

        private void OnEnable()
        {
            JudgementSystem.AnyJudged += OnJudged;
            JudgementSystem.AnyHoldReleased += OnHold;
            CombatController.AnyBegan += OnBattleBegan;
            CombatController.AnyEnded += OnBattleEnded;
            CombatController.AnyMoment += OnBattleMoment;
            GameSceneManager.SceneLoading += OnSceneLoading;
            Settings.FlagChanged += OnFlag;
            Settings.OptionsChanged += OnOptions;
            OverworldController.TalkStarted += OnTalkStarted;
            OverworldController.TalkEnded += OnTalkEnded;
            OverworldController.ObjectiveChanged += OnObjective;
            SceneManager.activeSceneChanged += OnSceneChanged;
            Telemetry.ConsentPromptChanged += Hold;

            Telemetry.AddContext("song", () => _battle != null ? _battle.songId : null);
            Telemetry.AddContext("song_t", () => _battle != null ? Mathf.Round(_battle.CurrentSongTime * 100f) / 100f : (object)null);
            Telemetry.Log("game_settings", Options());
            if (Telemetry.ConsentPromptOpen) Hold(true);
            StartCoroutine(FindPlayer());
            if (Benchmark.Requested) StartCoroutine(RunBenchmark());
        }

        private void OnDisable()
        {
            JudgementSystem.AnyJudged -= OnJudged;
            JudgementSystem.AnyHoldReleased -= OnHold;
            CombatController.AnyBegan -= OnBattleBegan;
            CombatController.AnyEnded -= OnBattleEnded;
            CombatController.AnyMoment -= OnBattleMoment;
            GameSceneManager.SceneLoading -= OnSceneLoading;
            Settings.FlagChanged -= OnFlag;
            Settings.OptionsChanged -= OnOptions;
            OverworldController.TalkStarted -= OnTalkStarted;
            OverworldController.TalkEnded -= OnTalkEnded;
            OverworldController.ObjectiveChanged -= OnObjective;
            SceneManager.activeSceneChanged -= OnSceneChanged;
            Telemetry.ConsentPromptChanged -= Hold;
        }

        private static Dictionary<string, object> Options()
        {
            var s = Settings.Instance;
            if (s == null) return new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                { "audio_offset_ms", s.audioOffsetMs },
                { "note_speed", s.noteScrollSpeed },
                { "difficulty", s.difficulty },
                { "master_volume", s.masterVolume },
                { "options", new RawJson(JsonUtility.ToJson(s.options)) },
            };
        }

        // ── battles ─────────────────────────────────────────────────────────
        private void OnBattleBegan(CombatController c)
        {
            _battle = c;
            _battleStart = Time.realtimeSinceStartup;
            string key = c.songId + "|" + c.chartId;
            _attempts.TryGetValue(key, out int n);
            _attempts[key] = ++n;
            var chart = c.chartData;
            Telemetry.Log("battle_start",
                "song", c.songId, "chart", c.chartId, "attempt", n,
                "enemy", Settings.Instance != null ? Settings.Instance.enemyName : null,
                "arcade", c.arcade, "tutorial", c.tutorialMode,
                "bpm", chart != null ? chart.bpm : 0f, "notes", chart != null ? chart.notes.Count : 0,
                "audio_offset_ms", Settings.Instance != null ? Settings.Instance.audioOffsetMs : 0f);
        }

        private void OnBattleEnded(CombatController c, BattleOutcome outcome)
        {
            var r = c.Summary();
            Telemetry.Log("battle_end",
                "song", c.songId, "chart", c.chartId, "outcome", outcome.ToString(),
                "score", r.score, "max_combo", r.maxCombo, "accuracy", r.accuracy, "grade", r.grade,
                "perfect", r.perfect, "good", r.good, "miss", r.miss, "total", r.total,
                "duration_s", Time.realtimeSinceStartup - _battleStart,
                "song_t", c.CurrentSongTime, "player_hp", c.PlayerHp, "enemy_hp", c.EnemyHp,
                "arcade", c.arcade, "tutorial", c.tutorialMode);
            if (_battle == c) _battle = null;
        }

        private void OnBattleMoment(CombatController c, string what) =>
            Telemetry.Log("battle_" + what, "song", c.songId, "chart", c.chartId, "song_t", c.CurrentSongTime);

        private void OnJudged(int lane, NoteType type, string result, float delta)
        {
            var c = _battle;
            Telemetry.Log("judge",
                "song", c != null ? c.songId : null, "chart", c != null ? c.chartId : null,
                "lane", lane, "note", type.ToString().ToLowerInvariant(), "result", result,
                "ms", float.IsNaN(delta) ? null : (object)(delta * 1000f),
                "song_t", c != null ? c.CurrentSongTime : 0f,
                "combo_before", c != null ? c.Combo : 0, "hp", c != null ? c.PlayerHp : 0);
        }

        private void OnHold(int lane, string result, float delta)
        {
            var c = _battle;
            Telemetry.Log("hold",
                "song", c != null ? c.songId : null, "chart", c != null ? c.chartId : null,
                "lane", lane, "result", result,
                "release_ms", float.IsNaN(delta) ? null : (object)(delta * 1000f),
                "song_t", c != null ? c.CurrentSongTime : 0f);
        }

        // ── the world ───────────────────────────────────────────────────────
        private void OnSceneLoading(GameSceneManager.SceneId id) => Telemetry.BeginSpan("load", "to", id.ToString());

        private void OnFlag(string flag, bool on) => Telemetry.Log("flag", "flag", flag, "on", on);

        private void OnOptions() => Telemetry.Log("options", Options());

        private void OnTalkStarted(string npc)
        {
            _talkWith = npc;
            _talkAt = Telemetry.SessionTime;
            Telemetry.Log("talk_start", "npc", npc);
        }

        private void OnTalkEnded(string npc)
        {
            float secs = _talkWith != null ? (float)(Telemetry.SessionTime - _talkAt) : 0f;
            Telemetry.Log("talk_end", "npc", npc, "secs", secs);
            _talkWith = null;
        }

        private void OnObjective(string text)
        {
            double now = Telemetry.SessionTime;
            Telemetry.Log("objective", "text", text, "prev_secs", _objectiveAt > 0 ? (float)(now - _objectiveAt) : 0f);
            _objectiveAt = now;
        }

        private void OnSceneChanged(Scene from, Scene to)
        {
            StartCoroutine(FindPlayer());
            if (_holding) Hold(true);
        }

        // whoever Cheri is in this scene, tracked for the heatmaps (players are built in Start)
        private IEnumerator FindPlayer()
        {
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                var ow = FindAnyObjectByType<OverworldController>();
                if (ow != null && ow.Player != null) { Telemetry.Track("overworld", ow.Player); yield break; }
                var club = FindAnyObjectByType<SpireController>();
                if (club != null && club.Player != null) { Telemetry.Track("club", club.Player); yield break; }
            }
        }

        // ── consent prompt: the game waits ──────────────────────────────────
        private void Hold(bool on)
        {
            _holding = on;
            CombatController.SuspendInput = on;
            var menu = FindAnyObjectByType<MainMenuController>();
            if (menu != null) menu.enabled = !on;
        }

        // ── -benchmark ──────────────────────────────────────────────────────
        private IEnumerator RunBenchmark()
        {
            string scene = Benchmark.Scene;
            if (string.IsNullOrEmpty(scene)) yield break; // the package begins one where we are
            // let the game finish booting (Bootstrap -> MainMenu) before going anywhere, or the
            // boot flow carries on to the menu after we've left
            float boot = Time.realtimeSinceStartup + 20f;
            while (SceneManager.GetActiveScene().name != "MainMenu" && Time.realtimeSinceStartup < boot) yield return null;
            yield return new WaitForSecondsRealtime(1.5f);
            if (!System.Enum.TryParse(scene, true, out GameSceneManager.SceneId id) || GameSceneManager.Instance == null)
            {
                Debug.LogWarning($"[Telemetry] -benchmarkScene {scene}: no such scene, benchmarking where we are");
                Benchmark.Begin();
                yield break;
            }
            string before = SceneManager.GetActiveScene().name;
            GameSceneManager.Instance.Goto(id, true);
            float until = Time.realtimeSinceStartup + 30f;
            while (SceneManager.GetActiveScene().name == before && Time.realtimeSinceStartup < until) yield return null;
            // let it settle: streaming, first-frame spikes
            yield return new WaitForSecondsRealtime(3f);
            Benchmark.Begin(id.ToString());
        }
    }
}
