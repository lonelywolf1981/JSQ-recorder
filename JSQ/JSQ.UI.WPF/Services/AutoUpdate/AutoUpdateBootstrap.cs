using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace JSQ.UI.WPF.Services.AutoUpdate;

public static class AutoUpdateBootstrap
{
    public static bool TryLaunchPendingUpdater()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var updaterRoot = Path.Combine(baseDir, ".jsq_updater");
            var statePath = Path.Combine(updaterRoot, "pending_update.json");
            if (!File.Exists(statePath))
                return false;

            var stateJson = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<AutoUpdateState>(stateJson);
            if (state == null || string.IsNullOrWhiteSpace(state.PackagePath) || !File.Exists(state.PackagePath))
                return false;

            var updaterExe = Path.Combine(baseDir, "JSQ.Updater.exe");
            if (!File.Exists(updaterExe))
                return false;

            var currentPid = Process.GetCurrentProcess().Id;

            var args =
                $"--installRoot \"{baseDir}\" " +
                $"--packagePath \"{state.PackagePath}\" " +
                $"--sha256 \"{state.Sha256}\" " +
                $"--restartExe \"JSQ.UI.WPF.exe\" " +
                $"--statePath \"{statePath}\" " +
                $"--waitPid {currentPid.ToString()}";

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = baseDir
            });

            return true;
        }
        catch
        {
            return false;
        }
    }
}
