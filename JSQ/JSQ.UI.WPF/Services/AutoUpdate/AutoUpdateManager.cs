using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF.Services.AutoUpdate;

public sealed class AutoUpdateManager : IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly Func<bool> _isRecordingActive;
    private readonly string _stateFilePath;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private Timer? _timer;
    private AutoUpdateState? _cachedState;

    public event Action<AutoUpdateStatus>? StatusChanged;

    public AutoUpdateManager(SettingsViewModel settings, Func<bool> isRecordingActive)
    {
        _settings = settings;
        _isRecordingActive = isRecordingActive;

        var updaterRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".jsq_updater");
        _cacheDirectory = Path.Combine(updaterRoot, "cache");
        _stateFilePath = Path.Combine(updaterRoot, "pending_update.json");
    }

    public void Start()
    {
        var interval = Math.Max(1, _settings.UpdateCheckIntervalMinutes);
        _timer = new Timer(async _ => await CheckForUpdatesAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(interval));
        _ = CheckForUpdatesAsync();
    }

    public void NotifyRuntimeStateChanged()
    {
        EmitStatus(_cachedState);
    }

    public async Task CheckForUpdatesAsync()
    {
        if (!_settings.AutoUpdateEnabled)
        {
            EmitStatus(null);
            return;
        }

        if (!await _checkLock.WaitAsync(0))
            return;

        try
        {
            var feedPath = (_settings.UpdateFeedPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(feedPath) || !Directory.Exists(feedPath))
            {
                EmitStatus(null);
                return;
            }

            var manifestPath = Path.Combine(feedPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                EmitStatus(null);
                return;
            }

            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<AutoUpdateManifest>(manifestJson);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.PackageFile))
            {
                EmitStatus(null);
                return;
            }

            if (!Version.TryParse(manifest.Version, out var remoteVersion))
            {
                EmitStatus(null);
                return;
            }

            var currentVersion = ResolveCurrentVersion();
            if (remoteVersion <= currentVersion)
            {
                ClearPendingState();
                EmitStatus(null);
                return;
            }

            var sourcePackage = Path.Combine(feedPath, manifest.PackageFile);
            if (!File.Exists(sourcePackage))
            {
                EmitStatus(null);
                return;
            }

            Directory.CreateDirectory(_cacheDirectory);
            var cachedPackageName = $"update-{manifest.Version}.zip";
            var cachedPackage = Path.Combine(_cacheDirectory, cachedPackageName);

            // Пересчитываем хэш кэшированного файла чтобы обнаружить битые копии
            // (прерванная загрузка с сети даёт файл верного размера, но неверного хэша).
            var needsCopy = !File.Exists(cachedPackage)
                || new FileInfo(cachedPackage).Length != new FileInfo(sourcePackage).Length
                || !ValidateHash(cachedPackage, manifest.Sha256);
            if (needsCopy)
                File.Copy(sourcePackage, cachedPackage, overwrite: true);

            if (!ValidateHash(cachedPackage, manifest.Sha256))
            {
                StatusChanged?.Invoke(new AutoUpdateStatus
                {
                    IsUpdateReady = false,
                    Version = manifest.Version,
                    Message = "Обновление обнаружено, но не прошло проверку целостности"
                });
                return;
            }

            var state = new AutoUpdateState
            {
                PendingVersion = manifest.Version,
                PackagePath = cachedPackage,
                Sha256 = manifest.Sha256,
                PreparedAt = DateTime.UtcNow
            };

            SavePendingState(state);
            _cachedState = state;
            EmitStatus(state);
        }
        catch
        {
            // Ошибки обновления не должны ломать основную работу приложения.
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private static Version ResolveCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info) && Version.TryParse(info, out var parsedInfo))
            return parsedInfo;

        var fileVersion = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion) && Version.TryParse(fileVersion, out var parsedFile))
            return parsedFile;

        return asm.GetName().Version ?? new Version(0, 0, 0, 0);
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

    private void SavePendingState(AutoUpdateState state)
    {
        var dir = Path.GetDirectoryName(_stateFilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_stateFilePath, json);
    }

    private void ClearPendingState()
    {
        _cachedState = null;
        if (File.Exists(_stateFilePath))
            File.Delete(_stateFilePath);
    }

    private void EmitStatus(AutoUpdateState? state)
    {
        if (state == null)
        {
            StatusChanged?.Invoke(new AutoUpdateStatus());
            return;
        }

        var message = _isRecordingActive()
            ? $"Доступно обновление v{state.PendingVersion}. Остановите запись и перезапустите приложение для применения."
            : $"Доступно обновление v{state.PendingVersion}. Перезапустите приложение для применения.";

        StatusChanged?.Invoke(new AutoUpdateStatus
        {
            IsUpdateReady = true,
            Version = state.PendingVersion,
            Message = message
        });
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _checkLock.Dispose();
    }
}
