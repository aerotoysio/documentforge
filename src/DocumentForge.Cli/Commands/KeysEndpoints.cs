using DocumentForge.Cli.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// Issue #73 — admin REST surface for managed (persisted) scoped API
/// keys. The Studio "Keys" page calls these; operators with the admin
/// bearer can also drive them by curl. Keys live in the auto-attached
/// <c>_system._keys</c> collection via <see cref="KeyStore"/>; the
/// in-memory cache means lookups stay O(1) on the hot path even with
/// hundreds of keys.
///
/// <para>
/// All endpoints are admin-scoped (the existing legacy <c>apiKey</c>
/// or a key carrying <c>*</c>). Issuing a tenant key shouldn't be
/// possible from a tenant-scoped key.
/// </para>
/// </summary>
public static class KeysEndpoints
{
    public static void Map(IEndpointRouteBuilder app, KeyStore keyStore)
    {
        // List every stored key. Returns id + scopes + description +
        // createdAt — never the hash, never the plaintext.
        app.MapGet("/admin/keys", (HttpContext ctx) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var entries = keyStore.List();
            return Results.Ok(new { count = entries.Count, keys = entries });
        });

        // Mint a new key. The plaintext secret is in the response
        // exactly ONCE — the operator must copy it now or rotate later.
        // The hash is what's persisted; the secret never touches disk.
        app.MapPost("/admin/keys", (HttpContext ctx, CreateKeyRequest req) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            try
            {
                var result = keyStore.CreateKey(req.Scopes ?? new(), req.Description);
                return Results.Created($"/admin/keys/{result.Id}", new
                {
                    id = result.Id,
                    // The ONE-TIME plaintext. Caller must store this now.
                    secret = result.Secret,
                    scopes = result.Stored.Scopes,
                    description = result.Stored.Description,
                    createdAt = result.Stored.CreatedAt,
                    warning = "Store the 'secret' now — it cannot be retrieved later. Lost key = revoke + regenerate.",
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Revoke a key. Effective immediately — the next request bearing
        // that secret gets 401. No undo; the only path back is to mint
        // a fresh key with the same scopes.
        app.MapDelete("/admin/keys/{id}", (HttpContext ctx, string id) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var removed = keyStore.RevokeKey(id);
            if (!removed)
                return Results.NotFound(new { error = $"Key '{id}' not found." });
            return Results.Ok(new { id, revoked = true });
        });
    }
}

/// <summary>Body for POST /admin/keys.</summary>
public record CreateKeyRequest(List<string>? Scopes, string? Description = null);
