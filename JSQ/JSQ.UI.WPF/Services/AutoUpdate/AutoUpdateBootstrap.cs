using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace JSQ.UI.WPF.Services.AutoUpdate;

public static class AutoUpdateBootstrap
{
    public static bool TryLaunchPendingUpdater(out string failureMessage)
    {
        failureMessage = string.Empty;

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
            {
                TryDisablePendingState(statePath);
                failureMessage = "Обнаружен поврежденный pending_update.json. Запуск выполнен без автообновления.";
                return false;
            }

            var updaterExe = Path.Combine(baseDir, "JSQ.Updater.exe");
            if (!File.Exists(updaterExe))
            {
                TryDisablePendingState(statePath);
                failureMessage = "Не найден JSQ.Updater.exe. Запуск выполнен без автообновления.";
                return false;
            }

            var currentPid = Process.GetCurrentProcess().Id;

            // ВАЖНО: AppDomain.CurrentDomain.BaseDirectory всегда заканчивается на '\'.
            // При передаче в quoted аргумент вида "--installRoot \"C:\path\"" пара \",
            // по правилам Windows-парсера аргументов, трактуется как экранированная кавычка
            // (литеральный '"'), а не как закрывающая кавычка. В результате весь хвост
            // командной строки попадает в значение installRoot и апдейтер падает.
            // Решение: убрать завершающий разделитель перед подстановкой в кавычки.
            var installRootArg = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var packagePathArg = state.PackagePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var statePathArg   = statePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var args =
                $"--installRoot \"{installRootArg}\" " +
                $"--packagePath \"{packagePathArg}\" " +
                $"--sha256 \"{state.Sha256}\" " +
                $"--restartExe \"JSQ.UI.WPF.exe\" " +
                $"--statePath \"{statePathArg}\" " +
                $"--waitPid {currentPid}";

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = baseDir
            });

            if (process == null)
            {
                TryDisablePendingState(statePath);
                failureMessage = "Не удалось запустить процесс автообновления. Запуск выполнен без обновления.";
                return false;
            }

            Thread.Sleep(200);
            if (process.HasExited)
            {
                if (process.ExitCode != 0)
                {
                    var code = process.ExitCode;
                    TryDisablePendingState(statePath);
                    failureMessage = $"Процесс автообновления завершился с ошибкой (код {code}). Запуск выполнен без обновления.";
                    return false;
                }

                // Успешное обновление может завершиться быстро на маленьком пакете.
                return true;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureMessage = $"Ошибка запуска автообновления: {ex.Message}. Запуск выполнен без обновления.";
            return false;
        }
    }

    private static void TryDisablePendingState(string statePath)
    {
        try
        {
            if (!File.Exists(statePath))
                return;

            var dir = Path.GetDirectoryName(statePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            var archive = Path.Combine(dir, $"pending_update.failed.{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.Move(statePath, archive);
        }
        catch
        {
            try
            {
                File.Delete(statePath);
            }
            catch
            {
                // Игнорируем любые ошибки очистки, чтобы не блокировать запуск приложения.
            }
        }
    }
}
