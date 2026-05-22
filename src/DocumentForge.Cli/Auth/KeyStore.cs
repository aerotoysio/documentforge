using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Engine;

namespace DocumentForge.Cli.Auth;

/// <summary>
/// Issue #73 (follow-up to #72) — persistent storage for scoped API
/// keys. Keys live in the auto-attached <c>_system</c> database's
/// <c>_keys</c> collection (so they replicate + survive restarts like
/// any other DocumentForge data) but are also held in an in-memory
/// cache so the auth middleware does a single hash-table lookup per
/// request, not a database query.
///
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item>Service startup → <see cref="Reload"/> reads every doc in
///     <c>_keys</c> into the cache.</item>
///   <item>Auth middleware → <see cref="TryAuthenticate"/> hashes the
///     incoming Bearer token and looks it up in O(1).</item>
///   <item><c>POST /admin/keys</c> → <see cref="CreateKey"/> writes
///     to the collection and updates the cache. The plaintext secret
///     is returned ONCE to the caller and never stored anywhere.</item>
///   <item><c>DELETE /admin/keys/{id}</c> → <see cref="RevokeKey"/>
///     deletes the doc and evicts the cache entry.</item>
/// </list>
/// </para>
///
/// <para>
/// Hashing: SHA-256 of the UTF-8 bytes of the token, hex-encoded. We
/// don't bcrypt/argon2 because the tokens are 32-byte random secrets
/// (256 bits of entropy); the slow-by-design KDFs exist to defeat
/// dictionary attacks on low-entropy human-chosen passwords, which
/// these aren't. SHA-256 is fast + collision-resistant + sufficient.
/// </para>
/// </summary>
public sealed class KeyStore
{
    private const string SystemDbName = "_system";
    // Collection name without underscore so the SQL lexer doesn't have
    // to special-case identifier rules. The containing DB is _system,
    // which already signals internal use.
    private const string KeysCollection = "keys";

    private readonly DatabaseRegistry _registry;
    private readonly object _lock = new();
    // hash → record. ConstantTimeEquals is only relevant against an
    // attacker who can time the response — a HashSet lookup leaks
    // existence-by-position-in-bucket but the bucket is one of many,
    // and the lookup is followed by string compare in any case.
    private readonly Dictionary<string, StoredKey> _byHash
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredKey> _byId
        = new(StringComparer.OrdinalIgnoreCase);

    public KeyStore(DatabaseRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>True once the _system DB exists + reload has run at
    /// least once. While false the auth middleware skips KeyStore
    /// lookups (bootstrap window — only the config admin key works).</summary>
    public bool Ready { get; private set; }

    /// <summary>Read the entire <c>_keys</c> collection into the cache.
    /// Called once at service startup. Cheap — keys are small docs and
    /// even a thousand-key deployment is microseconds to load.</summary>
    public void Reload()
    {
        var systemDb = _registry.TryGet(SystemDbName);
        if (systemDb is null)
        {
            // No _system DB attached yet (e.g. first-boot before
            // ServeCommand finishes wiring). Stay "not ready" and
            // succeed silently — the next Reload() picks it up.
            return;
        }

        lock (_lock)
        {
            _byHash.Clear();
            _byId.Clear();
            var result = systemDb.Execute($"SELECT * FROM {KeysCollection}");
            if (result.Success && result.Documents is not null)
            {
                foreach (var doc in result.Documents)
                {
                    var stored = StoredKey.FromDocument(doc);
                    if (stored is null) continue;
                    _byHash[stored.KeyHash] = stored;
                    _byId[stored.Id] = stored;
                }
            }
            Ready = true;
        }
    }

    /// <summary>O(1) lookup. Returns null when the token doesn't match
    /// any stored key; otherwise an <see cref="AuthContext"/> built from
    /// the stored scopes.</summary>
    public AuthContext? TryAuthenticate(string token)
    {
        if (!Ready || string.IsNullOrEmpty(token)) return null;
        var hash = HashToken(token);
        lock (_lock)
        {
            if (!_byHash.TryGetValue(hash, out var stored)) return null;
            return AuthContext.FromScopes(stored.Scopes, stored.Description ?? $"key:{stored.Id[..8]}");
        }
    }

    /// <summary>Mint a new key with the given scopes. Returns the
    /// plaintext secret — caller is responsible for showing it ONCE
    /// to the operator. Never returned again; lost = revoke + recreate.</summary>
    public CreateKeyResult CreateKey(IEnumerable<string> scopes, string? description)
    {
        var scopesList = scopes?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
            ?? new List<string>();
        if (scopesList.Count == 0)
            throw new ArgumentException("At least one scope is required.", nameof(scopes));

        var systemDb = _registry.TryGet(SystemDbName)
            ?? throw new InvalidOperationException(
                "_system DB is not attached. Cannot persist keys.");

        var secret = GenerateSecret();
        var hash = HashToken(secret);
        var id = Guid.NewGuid().ToString("N");

        var stored = new StoredKey(id, hash, scopesList, description, DateTime.UtcNow);
        var json = JsonSerializer.Serialize(new
        {
            id,
            keyHash = hash,
            scopes = scopesList,
            description,
            createdAt = stored.CreatedAt.ToString("O"),
        });
        systemDb.Insert(KeysCollection, json);

        lock (_lock)
        {
            _byHash[hash] = stored;
            _byId[id] = stored;
        }

        return new CreateKeyResult(id, secret, stored);
    }

    /// <summary>Revoke a key by id. Returns false when the id isn't
    /// known. Effective immediately — the next request bearing that
    /// token gets 401.</summary>
    public bool RevokeKey(string id)
    {
        StoredKey? stored;
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out stored)) return false;
            _byHash.Remove(stored.KeyHash);
            _byId.Remove(id);
        }

        var systemDb = _registry.TryGet(SystemDbName);
        if (systemDb is not null)
        {
            // Remove from disk too. The SQL engine doesn't have parameterised
            // queries yet so we inline the id — safe because GUIDs only
            // contain hex characters, can't break out of the quote.
            systemDb.Execute($"DELETE FROM {KeysCollection} WHERE id = '{id}'");
        }
        return true;
    }

    /// <summary>Public projection of every stored key — id + scopes +
    /// description + createdAt. Never includes the hash or plaintext.</summary>
    public IReadOnlyList<KeyInfo> List()
    {
        lock (_lock)
        {
            return _byId.Values
                .Select(s => new KeyInfo(s.Id, s.Scopes.ToList(), s.Description, s.CreatedAt))
                .OrderBy(k => k.CreatedAt)
                .ToList();
        }
    }

    public int Count
    {
        get { lock (_lock) return _byId.Count; }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>SHA-256(token) → lowercase hex. The tokens are 32-byte
    /// random secrets so a fast hash is fine; slow KDFs (bcrypt, argon2)
    /// solve a problem we don't have here.</summary>
    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Generate a fresh 32-byte URL-safe secret. The "df_"
    /// prefix makes leaked tokens recognisable in log scrapers + GitHub
    /// secret scanners.</summary>
    private static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "df_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed record StoredKey(
    string Id,
    string KeyHash,
    List<string> Scopes,
    string? Description,
    DateTime CreatedAt)
{
    public static StoredKey? FromDocument(Document.BsonDocument doc)
    {
        try
        {
            if (!doc.ContainsKey("id") || !doc.ContainsKey("keyHash") || !doc.ContainsKey("scopes"))
                return null;
            var id = doc["id"].AsString;
            var hash = doc["keyHash"].AsString;
            string? description = null;
            if (doc.ContainsKey("description") && doc["description"].Type == Document.BsonType.String)
                description = doc["description"].AsString;
            // The engine recognises ISO 8601 strings and stores them as
            // native DateTime — so the BsonValue could be either Type.String
            // (we round-tripped a fresh insert via SQL) or Type.DateTime
            // (we Insert()'d JSON and the parser typed it). Handle both.
            DateTime createdAt;
            var ca = doc["createdAt"];
            if (ca.Type == Document.BsonType.DateTime)
                createdAt = ca.AsDateTime.UtcDateTime;
            else
                createdAt = DateTime.Parse(ca.AsString,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);
            var scopes = new List<string>();
            var scopesValue = doc["scopes"];
            if (scopesValue.Type == Document.BsonType.Array)
            {
                foreach (var s in scopesValue.AsArray.Items)
                    if (s.Type == Document.BsonType.String) scopes.Add(s.AsString);
            }
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(hash) || scopes.Count == 0)
                return null;
            return new StoredKey(id, hash, scopes, description, createdAt);
        }
        catch { return null; }
    }
}

public sealed record KeyInfo(string Id, List<string> Scopes, string? Description, DateTime CreatedAt);

public sealed record CreateKeyResult(string Id, string Secret, StoredKey Stored);
