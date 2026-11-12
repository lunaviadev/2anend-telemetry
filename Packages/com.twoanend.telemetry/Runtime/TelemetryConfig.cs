using System;
using UnityEngine;

namespace TwoAnEnd.Playtest
{
    /// How telemetry behaves, read from Resources/telemetry_config.json (JsonUtility, so any
    /// field left out keeps its default). Keep the real file out of git: it holds the
    /// Supabase key. telemetry_config.example.json shows every field.
    [Serializable]
    public class TelemetryConfig
    {
        /// Master switch. Off: nothing runs at all.
        public bool enabled = true;
        /// Start a session as soon as the game boots (otherwise call Telemetry.Start yourself).
        public bool autoStart = true;
        /// Show the built-in consent prompt when consent isn't known. Turn this off if the
        /// game's own survey / consent screen calls Telemetry.SetConsent instead.
        public bool askConsent = true;
        /// Ask again every launch (right for shared playtest machines), or remember the answer.
        public bool askEverySession = true;
        /// In the editor, count as consented without asking (the developer testing).
        public bool editorAutoConsent = true;
        /// A small "PLAYTEST · code" corner mark while recording, so videos and screenshots
        /// can be matched to their logs.
        public bool watermark = true;
        /// Name shown on the consent prompt and written into every session header.
        public string studyName = "Playtest";

        /// Seconds between performance roll-ups (avg FPS, 1% / 0.1% lows, percentiles, memory).
        public float perfInterval = 5f;
        /// A frame slower than this (ms) is logged as a hitch, with what was happening.
        public float hitchMs = 50f;
        /// Seconds between position samples of tracked transforms.
        public float positionInterval = 1f;
        /// Position samples per "path" event.
        public int pathBatch = 10;
        /// Key that toggles the live overlay (a KeyCode name).
        public string overlayKey = "F3";

        /// Supabase project URL (https://xxxx.supabase.co) and its public anon key. Both
        /// empty: local files only.
        public string supabaseUrl = "";
        public string supabaseAnonKey = "";
        public string supabaseTable = "telemetry_events";
        /// The database function uploads go through (it inserts and skips duplicates).
        public string supabaseFunction = "ingest_telemetry";
        /// Seconds between uploads.
        public float uploadInterval = 10f;
        /// Upload sessions played in the editor too (off: they stay local).
        public bool uploadFromEditor = false;

        public bool SupabaseConfigured => !string.IsNullOrEmpty(supabaseUrl) && !string.IsNullOrEmpty(supabaseAnonKey);

        public static TelemetryConfig Load()
        {
            var cfg = new TelemetryConfig();
            var ta = Resources.Load<TextAsset>("telemetry_config");
            if (ta != null)
            {
                try { JsonUtility.FromJsonOverwrite(ta.text, cfg); }
                catch (Exception e) { Debug.LogWarning($"[Telemetry] telemetry_config.json couldn't be read, using defaults: {e.Message}"); }
            }
            return cfg;
        }
    }
}
