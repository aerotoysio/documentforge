using Microsoft.AspNetCore.Http;

namespace DocumentForge.Cli.Auth;

/// <summary>
/// Issue #72 — endpoint-side scope enforcement. Endpoints call these
/// at the top of their handler; a non-null return is an
/// already-formed <see cref="IResult"/> the caller returns immediately.
///
///   <code>
///     app.MapPost("/databases", (HttpContext ctx, CreateDatabaseRequest req) => {
///         if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
///         // … rest of handler
///     });
///   </code>
///
/// Centralising the 403 response shape keeps the wire surface
/// uniform across the dozen-ish routes that share these checks.
/// </summary>
public static class ScopeCheck
{
    /// <summary>Admin-only gate. Used for /databases CRUD, /services,
    /// /routers, /admin/*, and replication topology endpoints — the
    /// ops that affect the whole node.</summary>
    public static IResult? RequireAdmin(HttpContext ctx)
    {
        var auth = ctx.GetAuth();
        if (auth.CanAdmin) return null;
        return Forbid("admin", auth);
    }

    /// <summary>Database-scoped read. Use on GET /db/{name}/...,
    /// GET /collections/..., POST /query, etc.</summary>
    public static IResult? RequireDbRead(HttpContext ctx, string database)
    {
        if (DenyIfReserved(database) is { } reserved) return reserved;
        var auth = ctx.GetAuth();
        if (auth.CanReadDb(database)) return null;
        return Forbid($"read db:{database}", auth);
    }

    /// <summary>Database-scoped write. Use on POST /collections/{name},
    /// PUT, DELETE, /index, /seed, /db/{name}/collections/* writes.</summary>
    public static IResult? RequireDbWrite(HttpContext ctx, string database)
    {
        if (DenyIfReserved(database) is { } reserved) return reserved;
        var auth = ctx.GetAuth();
        if (auth.CanWriteDb(database)) return null;
        return Forbid($"write db:{database}", auth);
    }

    /// <summary>
    /// Returns a 403 when <paramref name="database"/> is reserved (see
    /// <see cref="IsReservedDatabase"/>), else null. Baked into the db-scoped
    /// read/write checks, and called explicitly by admin-gated routes that
    /// still target a database by name (e.g. per-DB replication control) so a
    /// reserved DB can't be routed there either.
    /// </summary>
    public static IResult? DenyIfReserved(string database) =>
        IsReservedDatabase(database) ? ForbidReserved(database) : null;

    /// <summary>
    /// Issue #105 — reserved databases (name starts with '_', e.g. the
    /// <c>_system</c> credential store that holds API-key hashes) are managed
    /// only through the admin plane (<c>/admin/keys</c> + KeyStore, backup /
    /// PITR jobs). They must NEVER be reachable through the document data
    /// plane, or a broadly-scoped key (<c>db:*</c>, or even <c>db:_system</c>)
    /// could read/write the key store via <c>?database=_system</c> or
    /// <c>/db/_system/...</c>. The block is unconditional — no scope, not even
    /// admin, grants data-plane access to these.
    /// </summary>
    public static bool IsReservedDatabase(string database) =>
        !string.IsNullOrEmpty(database) && database[0] == '_';

    private static IResult ForbidReserved(string database) =>
        Results.Json(new
        {
            error = $"Forbidden — '{database}' is a reserved system database and is not accessible " +
                    "via the data plane. Manage it through the admin API (e.g. /admin/keys).",
            reserved = true,
        }, statusCode: 403);

    private static IResult Forbid(string required, AuthContext auth)
    {
        return Results.Json(new
        {
            error = $"Forbidden — this Bearer token does not carry '{required}' scope.",
            matchedKey = string.IsNullOrEmpty(auth.MatchedKeyLabel) ? null : auth.MatchedKeyLabel,
            required,
        }, statusCode: 403);
    }
}
