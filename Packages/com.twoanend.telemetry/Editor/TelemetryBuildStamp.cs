using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace TwoAnEnd.Playtest.Editor
{
    /// Stamps the git commit into builds (Resources/telemetry_build.txt, "3fa9c21" or
    /// "3fa9c21-dirty" when there are uncommitted changes), so every session says exactly
    /// which code it ran. The file is removed again after the build.
    internal sealed class TelemetryBuildStamp : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string StampPath = "Assets/Resources/telemetry_build.txt";
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            string commit = SessionInfo.Git("rev-parse --short HEAD");
            if (string.IsNullOrEmpty(commit)) return;
            bool dirty = !string.IsNullOrWhiteSpace(SessionInfo.Git("status --porcelain --untracked-files=no"));
            Directory.CreateDirectory(Path.GetDirectoryName(StampPath));
            File.WriteAllText(StampPath, commit + (dirty ? "-dirty" : ""));
            AssetDatabase.ImportAsset(StampPath);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (File.Exists(StampPath)) AssetDatabase.DeleteAsset(StampPath);
        }
    }
}
