using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DocumentForge.Cli.Auth;

/// <summary>
/// Issue #72 — per-request authorization context. Built by the
/// auth middleware once per request from the Bearer token and stashed
/// on <see cref="HttpContext.Items"/> under <see cref="ContextKey"/>.
/// Endpoint handlers ask it questions:
///
///   <code>
///     var auth = ctx.GetAuth();
///     if (!auth.CanAdmin) return Results.Forbid();
///     if (!auth.CanWriteDb("tenant_a")) return Results.Forbid();
///   </code>
///
/// <para>
/// The middleware ensures the context is always present — anonymous
/// requests get an instance with <see cref="IsAnonymous"/> = true and
/// no permissions. Dev mode (no <c>ApiKey</c> / no <c>ScopedKeys</c>
/// configured) yields an anonymous request that's treated as admin
/// because the operator opted out of auth entirely.
/// </para>
/// </summary>
public sealed class AuthContext
{
    public const string ContextKey = "dfdb.auth";

    /// <summary>True when no Bearer token was supplied. In dev mode
    /// (no keys configured) this still returns true; <see cref="CanAdmin"/>
    /// is the right gate.</summary>
    public bool IsAnonymous { get; init; }

    /// <summary>True when the request matched the admin key (or the
    /// scope <c>*</c> on a scoped key) or when dev mode is on.</summary>
    public bool CanAdmin { get; init; }

    /// <summary>True when this token covers every database for the
    /// data plane. Implied by admin. Set by the scope <c>db:*</c>.</summary>
    public bool CanAccessAllDbs { get; init; }

    /// <summary>Per-DB access. Key = lowercase DB name, Value =
    /// (read, write) flags. Admin / db:* override.</summary>
    public IReadOnlyDictionary<string, DbAccess> DbAccess { get; init; }
        = new Dictionary<string, DbAccess>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Free-form label of the matched key, exposed in 401 logs.
    /// Empty for anonymous / dev-mode.</summary>
    public string MatchedKeyLabel { get; init; } = "";

    public bool CanReadDb(string database)
    {
        if (CanAdmin || CanAccessAllDbs) return true;
        return DbAccess.TryGetValue(database, out var acc) && acc.Read;
    }

    public bool CanWriteDb(string database)
    {
        if (CanAdmin || CanAccessAllDbs) return true;
        return DbAccess.TryGetValue(database, out var acc) && acc.Write;
    }

    public static AuthContext DevMode() => new()
    {
        IsAnonymous = true,
        CanAdmin = true,
        CanAccessAllDbs = true,
        MatchedKeyLabel = "dev-mode (no auth configured)",
    };

    public static AuthContext Anonymous() => new()
    {
        IsAnonymous = true,
        CanAdmin = false,
        MatchedKeyLabel = "anonymous",
    };

    public static AuthContext FromScopes(IEnumerable<string> scopes, string label)
    {
        bool isAdmin = false, allDbs = false;
        var perDb = new Dictionary<string, DbAccess>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in scopes)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) continue;
            if (s == "*") { isAdmin = true; continue; }
            if (s == "db:*") { allDbs = true; continue; }
            if (!s.StartsWith("db:", StringComparison.OrdinalIgnoreCase)) continue;

            // db:<name>[:read|:write]
            var parts = s.Substring(3).Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var dbName = parts[0];
            var mode = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
            var write = mode is null or "write" or "rw";
            var read  = true;
            if (perDb.TryGetValue(dbName, out var existing))
            {
                perDb[dbName] = new DbAccess(existing.Read || read, existing.Write || write);
            }
            else
            {
                perDb[dbName] = new DbAccess(read, write);
            }
        }
        return new AuthContext
        {
            IsAnonymous = false,
            CanAdmin = isAdmin,
            CanAccessAllDbs = isAdmin || allDbs,
            DbAccess = perDb,
            MatchedKeyLabel = label,
        };
    }
}

public readonly record struct DbAccess(bool Read, bool Write);

/// <summary>Cheap extension shim — endpoint code reads
/// <c>ctx.GetAuth()</c> instead of fishing through HttpContext.Items.</summary>
public static class AuthContextExtensions
{
    public static AuthContext GetAuth(this HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(AuthContext.ContextKey, out var raw)
            && raw is AuthContext a) return a;
        // No middleware? Treat as anonymous — safest default.
        return AuthContext.Anonymous();
    }

    /// <summary>Constant-time comparison so two-bytes-different-from-the-front
    /// keys take the same number of cycles to reject as identical-prefix
    /// keys. Crypto-101 — not strictly necessary for Bearer tokens but
    /// it's two lines and removes a real-world attack vector.</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
