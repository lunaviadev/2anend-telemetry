using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TwoAnEnd.Playtest
{
    /// Who and what a session is: ids, the short code testers type into the survey, the build,
    /// and the machine it ran on. Nothing personal: no device name, no hardware serial, no
    /// account; the player id is a random number kept in PlayerPrefs.
    public static class SessionInfo
    {
        // no 0/O, 1/I/L: easy to read off a screen and type into a form
        private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private const string PlayerKey = "twoanend.telemetry.pid";

        public static string NewSessionId() => Guid.NewGuid().ToString("N");

        /// A short, stable code for a session id: "7F3K-Q2".
        public static string CodeFor(string sessionId)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                foreach (char ch in sessionId ?? "") { h ^= ch; h *= 1099511628211UL; }
            }
            var sb = new StringBuilder(7);
            ulong n = (ulong)CodeAlphabet.Length;
            for (int i = 0; i < 6; i++)
            {
                if (i == 4) sb.Append('-');
                sb.Append(CodeAlphabet[(int)(h % n)]);
                h /= n;
            }
            return sb.ToString();
        }

        /// An anonymous id for this install, so sessions by the same tester can be grouped.
        public static string PlayerId()
        {
            string id = PlayerPrefs.GetString(PlayerKey, "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 12);
                PlayerPrefs.SetString(PlayerKey, id);
                PlayerPrefs.Save();
            }
            return id;
        }

        /// "0.1.0+3fa9c21": the app version and the commit it was built from (stamped into
        /// Resources/telemetry_build.txt at build time; read live from git in the editor).
        public static string BuildTag()
        {
            var ta = Resources.Load<TextAsset>("telemetry_build");
            string commit = ta != null ? ta.text.Trim() : "";
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(commit))
            {
                commit = Git("rev-parse --short HEAD");
                commit = string.IsNullOrEmpty(commit) ? "editor" : commit + "-editor";
            }
#endif
            return string.IsNullOrEmpty(commit) ? Application.version : Application.version + "+" + commit;
        }

        public static Dictionary<string, object> Header(TelemetryConfig cfg)
        {
            var res = Screen.currentResolution;
            return new Dictionary<string, object>
            {
                { "study", cfg.studyName },
                { "telemetry_version", Telemetry.Version },
                { "build", Telemetry.Build },
                { "player_id", Telemetry.PlayerId },
                { "session_code", Telemetry.SessionCode },
                { "product", Application.productName },
                { "unity", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "editor", Application.isEditor },
                { "dev_build", Debug.isDebugBuild },
                { "os", SystemInfo.operatingSystem },
                { "device_type", SystemInfo.deviceType.ToString() },
                { "cpu", SystemInfo.processorType },
                { "cpu_cores", SystemInfo.processorCount },
                { "cpu_mhz", SystemInfo.processorFrequency },
                { "ram_mb", SystemInfo.systemMemorySize },
                { "gpu", SystemInfo.graphicsDeviceName },
                { "gpu_vendor", SystemInfo.graphicsDeviceVendor },
                { "gpu_api", SystemInfo.graphicsDeviceType.ToString() },
                { "vram_mb", SystemInfo.graphicsMemorySize },
                { "shader_level", SystemInfo.graphicsShaderLevel },
                { "screen", $"{Screen.width}x{Screen.height}" },
                { "display", $"{res.width}x{res.height}" },
                { "refresh_hz", (float)res.refreshRateRatio.value },
                { "fullscreen", Screen.fullScreenMode.ToString() },
                { "quality", QualitySettings.names.Length > 0 ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "" },
                { "vsync", QualitySettings.vSyncCount },
                { "target_fps", Application.targetFrameRate },
                { "frame_timing", FrameTimingManager.IsFeatureEnabled() },
                { "language", Application.systemLanguage.ToString() },
            };
        }

#if UNITY_EDITOR
        /// Runs git in the project folder (editor only), or "" if it isn't there.
        public static string Git(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    WorkingDirectory = System.IO.Path.GetDirectoryName(Application.dataPath),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return "";
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(3000)) return "";
                    return p.ExitCode == 0 ? o.Trim() : "";
                }
            }
            catch { return ""; }
        }
#endif
    }
}
