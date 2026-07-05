using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocumentForge.Studio.Core.Settings;

/// <summary>
/// Stores API keys and other secrets DPAPI-encrypted (per Windows user) in
/// secrets.json next to the rest of the Studio settings. Values on disk are
/// base64(ProtectedData); ids are opaque and referenced from
/// ConnectionDescriptor.ApiKeySecretId. Export/import round-trips plaintext
/// so a settings bundle can move to another machine or user profile —
/// DPAPI blobs themselves never leave this box usefully.
/// </summary>
public sealed class SecretStore
{
    private readonly string _filePath;
    private readonly Dictionary<string, string> _protected;

    public SecretStore(string directory)
    {
        _filePath = Path.Combine(directory, "secrets.json");
        _protected = File.Exists(_filePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath)) ?? new()
            : new();
    }

    public string Set(string? id, string plaintext)
    {
        id ??= Guid.NewGuid().ToString("N");
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        _protected[id] = Convert.ToBase64String(blob);
        Save();
        return id;
    }

    /// <summary>Null when the id is unknown or the blob can't be decrypted
    /// (e.g. copied from another user profile).</summary>
    public string? TryGet(string id)
    {
        if (!_protected.TryGetValue(id, out var b64)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool Delete(string id)
    {
        var removed = _protected.Remove(id);
        if (removed) Save();
        return removed;
    }

    /// <summary>Decrypts everything — only for the explicit, user-warned
    /// export flow.</summary>
    public Dictionary<string, string> ExportPlaintext()
    {
        var result = new Dictionary<string, string>();
        foreach (var id in _protected.Keys)
            if (TryGet(id) is { } value)
                result[id] = value;
        return result;
    }

    public void ImportPlaintext(IReadOnlyDictionary<string, string> secrets, bool replaceAll)
    {
        if (replaceAll) _protected.Clear();
        foreach (var (id, value) in secrets)
        {
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            _protected[id] = Convert.ToBase64String(blob);
        }
        Save();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_protected, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.Write(_filePath, json);
    }
}

/// <summary>Write-to-temp-then-move so a crash mid-save never leaves a
/// half-written settings file.</summary>
internal static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
