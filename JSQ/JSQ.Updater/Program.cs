using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace JSQ.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Объявляем ДО try — чтобы finally имел доступ при любом исходе
        string statePath   = string.Empty;
        string restartPath = string.Empty;
        int    exitCode    = 1;

        try
        {
            var map = ParseArgs(args);

            var installRoot = Require(map, "installRoot");
            var packagePath = Require(map, "packagePath");
            var expectedSha = map.TryGetValue("sha256",     out var sha) ? sha : string.Empty;
            var restartExe  = map.TryGetValue("restartExe", out var rx)  ? rx  : "JSQ.UI.WPF.exe";
            statePath       = map.TryGetValue("statePath",  out var sp)  ? sp  : string.Empty;
            restartPath     = Path.Combine(installRoot, restartExe);

            var waitPid = 0;
            if (map.TryGetValue("waitPid", out var pidText))
                int.TryParse(pidText, out waitPid);

            // Версия для отображения: "update-1.2.3.zip" → "v1.2.3"
            var version = ParseVersionFromPackagePath(packagePath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new UpdateProgressForm(version);

            // Запускаем работу из события Load — к этому моменту Handle создан
            // и BeginInvoke/Invoke работают корректно.
            form.Load += (_, __) =>
            {
                var worker = new Thread(() =>
                {
                    try
                    {
                        exitCode = RunUpdate(installRoot, packagePath, expectedSha, waitPid, form);
                    }
                    catch
                    {
                        exitCode = 1;
                    }
                    finally
                    {
                        // Показываем итоговое сообщение на 900 мс, потом закрываем форму
                        var msg = exitCode == 0
                            ? "Готово. Запуск приложения..."
                            : "Ошибка обновления. Запуск предыдущей версии...";
                        var prog = exitCode == 0 ? 100 : -1;

                        form.SetStatus(msg, prog);

                        Thread.Sleep(900);
                        form.BeginInvoke(new Action(form.Close));
                    }
                })
                {
                    IsBackground = true,
                    Name = "UpdateWorker"
                };
                worker.Start();
            };

            Application.Run(form);   // ← блокирует до закрытия формы
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            // ВСЕГДА удаляем state-файл — иначе при любой ошибке приложение
            // входит в бесконечный цикл: запуск → Shutdown → апдейтер падает → повтор
            if (!string.IsNullOrWhiteSpace(statePath) && File.Exists(statePath))
                try { File.Delete(statePath); } catch { }

            // ВСЕГДА запускаем приложение — при ошибке стартуют старые файлы,
            // что лучше чем не запуститься вообще
            if (!string.IsNullOrWhiteSpace(restartPath) && File.Exists(restartPath))
                try
                {
                    // UseShellExecute = true обязателен для WPF-приложений:
                    // с false + CreateNoWindow главное окно может не появиться.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName         = restartPath,
                        WorkingDirectory = Path.GetDirectoryName(restartPath)!,
                        UseShellExecute  = true
                    });
                }
                catch { }
        }

        return exitCode;
    }

    // ─── Основная логика обновления ──────────────────────────────────────────

    private static int RunUpdate(
        string installRoot,
        string packagePath,
        string expectedSha,
        int    waitPid,
        UpdateProgressForm form)
    {
        // Шаг 1: ждём завершения основного процесса
        if (waitPid > 0)
        {
            form.SetStatus("Ожидание завершения приложения...", 5);
            WaitForProcessExit(waitPid, TimeSpan.FromSeconds(45));
        }

        // Шаг 2: проверяем целостность архива
        form.SetStatus("Проверка целостности архива...", 20);
        if (!ValidateHash(packagePath, expectedSha))
            return 2;

        // Шаг 3: распаковка во временную папку
        form.SetStatus("Распаковка архива...", 35);
        var updaterRoot = Path.Combine(installRoot, ".jsq_updater");
        var stagingRoot = Path.Combine(updaterRoot, "staging", Guid.NewGuid().ToString("N"));
        var backupRoot  = Path.Combine(updaterRoot, "backup",  DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        ZipFile.ExtractToDirectory(packagePath, stagingRoot);

        // Шаг 4: копируем файлы из staging в installRoot
        var sourceFiles = Directory.GetFiles(stagingRoot, "*", SearchOption.AllDirectories);
        int total  = sourceFiles.Length;
        int copied = 0;

        foreach (var source in sourceFiles)
        {
            copied++;

            // Прогресс от 50% до 90% пропорционально числу файлов
            var progress = total > 0
                ? 50 + (int)((double)copied / total * 40)
                : 50;
            form.SetStatus($"Копирование файлов...  {copied} / {total}", progress);

            var rel = source.Substring(stagingRoot.Length).TrimStart(Path.DirectorySeparatorChar);

            if (ShouldSkipPackageFile(rel))
                continue;

            var target = Path.Combine(installRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Бэкап существующего файла
            if (File.Exists(target))
            {
                var backup = Path.Combine(backupRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: true);
            }

            // Сам апдейтер не перезаписываем — он сейчас запущен
            if (string.Equals(Path.GetFileName(target), "JSQ.Updater.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Copy(source, target, overwrite: true);
        }

        // Шаг 5: очистка временных файлов
        form.SetStatus("Очистка временных файлов...", 95);
        TryDeleteDirectory(stagingRoot);

        return 0;
    }

    // ─── Вспомогательные методы ──────────────────────────────────────────────

    private static string ParseVersionFromPackagePath(string packagePath)
    {
        var name   = Path.GetFileNameWithoutExtension(packagePath ?? string.Empty);
        const string prefix = "update-";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "v" + name.Substring(prefix.Length)
            : string.Empty;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                continue;
            var name  = key.Substring(2);
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
            // Процесс уже завершился — это нормально.
        }
        Thread.Sleep(300);
    }

    private static bool ValidateHash(string filePath, string expectedSha)
    {
        if (string.IsNullOrWhiteSpace(expectedSha))
            return true;

        using var stream = File.OpenRead(filePath);
        using var sha    = SHA256.Create();
        var hash   = sha.ComputeHash(stream);
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

        return normalized.EndsWith(".db",     StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);
    }
}
