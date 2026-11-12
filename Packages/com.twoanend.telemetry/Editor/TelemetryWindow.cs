using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TwoAnEnd.Playtest.Editor
{
    /// Window > 2ANEND > Telemetry: the session in play mode (code, consent, events, upload),
    /// the config, and the log folder (open it, see the sessions, delete them).
    public sealed class TelemetryWindow : EditorWindow
    {
        private const string ConfigPath = "Assets/Resources/telemetry_config.json";
        private Vector2 _scroll;

        [MenuItem("Window/2ANEND/Telemetry")]
        private static void Open() => GetWindow<TelemetryWindow>("Telemetry");

        [MenuItem("Tools/2ANEND Telemetry/Open Log Folder")]
        private static void OpenLogs()
        {
            Directory.CreateDirectory(Telemetry.LogDirectory);
            EditorUtility.RevealInFinder(Telemetry.LogDirectory + Path.DirectorySeparatorChar);
        }

        [MenuItem("Tools/2ANEND Telemetry/Create Demo Scene")]
        private static void CreateDemo()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject("Telemetry Demo").AddComponent<TelemetryDemo>();
            EditorUtility.DisplayDialog("Telemetry demo", "Press Play. D F J K hit notes, arrow keys walk, F3 shows the live overlay.\n\nSave the scene if you want to keep it.", "OK");
        }

        [MenuItem("Tools/2ANEND Telemetry/Create Config")]
        private static void CreateConfig()
        {
            if (File.Exists(ConfigPath)) { EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<TextAsset>(ConfigPath)); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, JsonUtility.ToJson(new TelemetryConfig(), true));
            AssetDatabase.ImportAsset(ConfigPath);
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<TextAsset>(ConfigPath));
            Debug.Log($"[Telemetry] wrote {ConfigPath}. Keep it out of git if it holds a Supabase key.");
        }

        private void OnInspectorUpdate()
        {
            if (Application.isPlaying) Repaint();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Session", EditorStyles.boldLabel);
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Enter play mode to see the live session.", MessageType.None);
            else if (!Telemetry.Started)
                EditorGUILayout.HelpBox("Telemetry isn't running (disabled in the config, or autoStart is off).", MessageType.Warning);
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Code", Telemetry.SessionCode, EditorStyles.boldLabel);
                    if (GUILayout.Button("Copy", GUILayout.Width(60))) EditorGUIUtility.systemCopyBuffer = Telemetry.SessionCode;
                }
                EditorGUILayout.LabelField("Session id", Telemetry.SessionId);
                EditorGUILayout.LabelField("Build", Telemetry.Build);
                EditorGUILayout.LabelField("Consent", Telemetry.Consent.ToString());
                EditorGUILayout.LabelField("Events", $"{Telemetry.RecordedCount} recorded / {Telemetry.EventCount} logged");
                if (Telemetry.LocalFile != null) EditorGUILayout.LabelField("File", Path.GetFileName(Telemetry.LocalFile.FilePath));
                var up = Telemetry.Upload;
                EditorGUILayout.LabelField("Upload", up == null ? "off" : $"{up.Status} · queued {up.Queued} · sent {up.Sent}");
                if (up != null && up.Failures > 0) EditorGUILayout.HelpBox(up.LastError, MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Grant consent")) Telemetry.SetConsent(true, "editor_window");
                    if (GUILayout.Button("Withdraw")) Telemetry.SetConsent(false, "editor_window");
                    if (GUILayout.Button("Flush")) Telemetry.Flush();
                }
                foreach (var kv in Telemetry.CountsByType) EditorGUILayout.LabelField("   " + kv.Key, kv.Value.ToString());
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Config", EditorStyles.boldLabel);
            if (!File.Exists(ConfigPath))
            {
                EditorGUILayout.HelpBox("No Assets/Resources/telemetry_config.json: running on defaults (local files only).", MessageType.Info);
                if (GUILayout.Button("Create config")) CreateConfig();
            }
            else
            {
                var cfg = TelemetryConfig.Load();
                EditorGUILayout.LabelField("Supabase", cfg.SupabaseConfigured ? cfg.supabaseUrl : "not set (local only)");
                EditorGUILayout.LabelField("Consent prompt", cfg.askConsent ? (cfg.askEverySession ? "every session" : "once") : "off (game asks)");
                if (GUILayout.Button("Select config")) Selection.activeObject = AssetDatabase.LoadAssetAtPath<TextAsset>(ConfigPath);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Logs", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(Telemetry.LogDirectory, GUILayout.Height(18));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open folder")) OpenLogs();
                if (GUILayout.Button("Delete all local data") &&
                    EditorUtility.DisplayDialog("Delete telemetry", "Delete every telemetry file on this machine (logs, summaries, the upload outbox)?", "Delete", "Cancel"))
                    Telemetry.DeleteLocalData();
            }
            if (Directory.Exists(Telemetry.LogDirectory))
            {
                var files = new DirectoryInfo(Telemetry.LogDirectory).GetFiles("*.jsonl");
                System.Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                for (int i = 0; i < files.Length && i < 20; i++)
                    EditorGUILayout.LabelField(files[i].Name, $"{files[i].Length / 1024f:0.0} KB");
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
