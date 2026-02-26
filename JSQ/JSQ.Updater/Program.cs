using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace JSQ.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var map = ParseArgs(args);

            var installRoot = Require(map, "installRoot");
            var packagePath = Require(map, "packagePath");
            var expectedSha = map.TryGetValue("sha256", out var sha) ? sha : string.Empty;
            var restartExe = map.TryGetValue("restartExe", out var restart) ? restart : "JSQ.UI.WPF.exe";
            var statePath = map.TryGetValue("statePath", out var state) ? state : string.Empty;

            if (map.TryGetValue("waitPid", out var pidText) && int.TryParse(pidText, out var pid) && pid > 0)
                WaitForProcessExit(pid, TimeSpan.FromSeconds(45));

            if (!ValidateHash(packagePath, expectedSha))
                return 2;

            var updaterRoot = Path.Combine(installRoot, ".jsq_updater");
            var stagingRoot = Path.Combine(updaterRoot, "staging", Guid.NewGuid().ToString("N"));
            var backupRoot = Path.Combine(updaterRoot, "backup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(backupRoot);

            ZipFile.ExtractToDirectory(packagePath, stagingRoot);

            var sourceFiles = Directory.GetFiles(stagingRoot, "*", SearchOption.AllDirectories);
            foreach (var source in sourceFiles)
            {
                var rel = source.Substring(stagingRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                if (ShouldSkipPackageFile(rel))
                    continue;

                var target = Path.Combine(installRoot, rel);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                }

                if (string.Equals(Path.GetFileName(target), "JSQ.Updater.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(source, target, overwrite: true);
            }

            TryDeleteDirectory(stagingRoot);

            if (!string.IsNullOrWhiteSpace(statePath) && File.Exists(statePath))
                File.Delete(statePath);

            var restartPath = Path.Combine(installRoot, restartExe);
            if (File.Exists(restartPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = restartPath,
                    WorkingDirectory = installRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }

            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                continue;

            var name = key.Substring(2);
            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            map[name] = value;
            i++;
        }
        return map;
    }

    private static string Require(Dictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing argument: {key}");
        return value;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch
        {
            // Процесс уже завершился.
        }

        Thread.Sleep(300);
    }

    private static bool ValidateHash(string filePath, string expectedSha)
    {
        if (string.IsNullOrWhiteSpace(expectedSha))
            return true;

        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        var actual = BitConverter.ToString(hash).Replace("-", string.Empty);
        return string.Equals(actual, expectedSha.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Временные данные очистятся при следующем запуске.
        }
    }

    private static bool ShouldSkipPackageFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            return false;

        return normalized.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);
    }
}
