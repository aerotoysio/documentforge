using System.Reflection;

namespace DocumentForge.Engine;

/// <summary>
/// Build-identification info surfaced by the engine's <c>GET /version</c> REST
/// endpoint (issue #36). Three fields, three independent sources, three
/// fallbacks each — operators always get something useful even when one of
/// them isn't wired up:
///
/// <list type="table">
///   <item><term>Sha</term><description>
///     Git commit SHA the build came from. Resolved from
///     <see cref="AssemblyInformationalVersionAttribute"/> (the C# compiler
///     embeds whatever <c>SourceRevisionId</c> MSBuild set as <c>+sha</c>),
///     then env var <c>DFDB_GIT_SHA</c>, then <c>"dev"</c>.</description></item>
///   <item><term>BuiltAtUtc</term><description>
///     UTC build timestamp. Resolved from the assembly's
///     <see cref="AssemblyMetadataAttribute"/> entry written by Directory.Build.props,
///     then env var <c>DFDB_BUILT_AT</c>, then the assembly file's last-write
///     time on disk.</description></item>
///   <item><term>Image</term><description>
///     Container image identifier (e.g. <c>ghcr.io/aerotoysio/documentforge:0.2.0</c>).
///     Env var <c>DFDB_IMAGE</c> only — the Dockerfile / k8s manifest /
///     Render config sets it. Null when running outside a container.</description></item>
/// </list>
///
/// <para>
/// Cached: the values are computed once on first read and never change for the
/// lifetime of the process. The endpoint is on the routine-polling path
/// (admin UIs, deploy verifiers), so we don't want a reflection sweep every
/// request.
/// </para>
/// </summary>
public static class BuildInfo
{
    private static readonly Lazy<string> _version = new(ResolveVersion);
    private static readonly Lazy<string> _sha = new(ResolveSha);
    private static readonly Lazy<DateTime?> _builtAt = new(ResolveBuiltAtUtc);
    private static readonly Lazy<string?> _image = new(ResolveImage);

    public static string Version => _version.Value;
    public static string Sha => _sha.Value;
    public static DateTime? BuiltAtUtc => _builtAt.Value;
    public static string? Image => _image.Value;

    private static string ResolveVersion()
    {
        // The MSBuild <Version> property (Directory.Build.props) lands as the
        // prefix of AssemblyInformationalVersion, before the '+sha' suffix.
        // Every caller that used to hardcode the release number reads this
        // instead, so a props bump is the whole story.
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plusIdx = info.IndexOf('+');
            return plusIdx > 0 ? info[..plusIdx] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "dev";
    }

    private static string ResolveSha()
    {
        // The compiler emits AssemblyInformationalVersion as
        // "<Version>+<SourceRevisionId>" when both are set. Split on '+' and
        // take the suffix; that's the SHA. If there's no '+', SourceRevisionId
        // wasn't populated at build time — fall through to env var or "dev".
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plusIdx = info.IndexOf('+');
            if (plusIdx >= 0 && plusIdx < info.Length - 1)
                return info[(plusIdx + 1)..];
        }

        var envSha = Environment.GetEnvironmentVariable("DFDB_GIT_SHA");
        if (!string.IsNullOrWhiteSpace(envSha)) return envSha;

        return "dev";
    }

    private static DateTime? ResolveBuiltAtUtc()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, "BuildTimeUtc", StringComparison.Ordinal)
                && DateTime.TryParse(attr.Value, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }

        var envBuilt = Environment.GetEnvironmentVariable("DFDB_BUILT_AT");
        if (!string.IsNullOrWhiteSpace(envBuilt)
            && DateTime.TryParse(envBuilt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var envDt))
        {
            return DateTime.SpecifyKind(envDt, DateTimeKind.Utc);
        }

        // Last resort: file write time of the loaded assembly. Useful for
        // dev-loop builds where we want SOMETHING rather than null.
        try
        {
            var path = asm.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTimeUtc(path);
        }
        catch { /* sandboxed runtime, single-file deploy — silently skip */ }

        return null;
    }

    private static string? ResolveImage()
    {
        // Container image identifier is never derivable from the running
        // assembly — it has to be injected by whatever built or scheduled
        // the container. We don't fabricate a value.
        var img = Environment.GetEnvironmentVariable("DFDB_IMAGE");
        return string.IsNullOrWhiteSpace(img) ? null : img;
    }
}
