using System;

namespace JSQ.UI.WPF.Services.AutoUpdate;

public class AutoUpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public string PackageFile { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool Mandatory { get; set; }
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime? PublishedAt { get; set; }
}

public class AutoUpdateState
{
    public string PendingVersion { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTime PreparedAt { get; set; }
}

public class AutoUpdateStatus
{
    public bool IsUpdateReady { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
