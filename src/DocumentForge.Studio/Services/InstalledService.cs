using System.IO;

namespace DocumentForge.Studio.Services;

/// <summary>
/// Reads the breadcrumb files the installer writes for the bundled local
/// service: datadir.txt next to the exe points at the data folder, which
/// holds port.txt and service-key.txt. Used to seed the first-run connection
/// (App) and to surface the service key in the API Keys page without the
/// user digging through the filesystem.
/// </summary>
public static class InstalledService
{
    /// <summary>The data folder the installer configured, read from datadir.txt
    /// next to the exe. Defaults to the DocumentForge standard.</summary>
    public static string DataDir =>
        ReadFile(Path.Combine(AppContext.BaseDirectory, "datadir.txt")) ?? @"C:\data\documentforge";

    /// <summary>The port the installer configured for the local service:
    /// port.txt in the data folder (pre-0.10.1: next to the exe). Defaults to
    /// 4300 (DocumentForge standard).</summary>
    public static int Port(string dataDir)
    {
        var text = ReadFile(Path.Combine(dataDir, "port.txt"))
                   ?? ReadFile(Path.Combine(AppContext.BaseDirectory, "port.txt"));
        return int.TryParse(text, out var p) && p is > 0 and <= 65535 ? p : 4300;
    }

    /// <summary>Where the installer-provisioned service key lives, when one
    /// does: service-key.txt in the data folder (pre-0.10.1: next to the exe).
    /// Null when neither location has a non-empty file.</summary>
    public static string? KeyFilePath
    {
        get
        {
            var inDataDir = Path.Combine(DataDir, "service-key.txt");
            if (ReadFile(inDataDir) is not null) return inDataDir;
            var legacy = Path.Combine(AppContext.BaseDirectory, "service-key.txt");
            return ReadFile(legacy) is not null ? legacy : null;
        }
    }

    /// <summary>The API key the installer provisioned for the bundled service.
    /// Null when absent — older installs, or the service component wasn't selected.</summary>
    public static string? Key(string dataDir) =>
        ReadFile(Path.Combine(dataDir, "service-key.txt"))
        ?? ReadFile(Path.Combine(AppContext.BaseDirectory, "service-key.txt"));

    /// <summary>Trimmed content of an installer-written file, or null when the
    /// file is missing, empty, or unreadable.</summary>
    private static string? ReadFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch { /* treat as absent */ }
        return null;
    }
}
