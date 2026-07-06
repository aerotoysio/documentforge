using System.Text.Json;
using DocumentForge.Cli.Auth;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Index;
using Microsoft.Extensions.Hosting;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// `dfdb serve` — run this node's REST API.
/// Reads configuration from a node.json file, CLI flags, or env vars.
/// </summary>
public static class ServeCommand
{
    public static int Run(string[] args)
    {
        var config = NodeConfig.Load(args);

        var builder = WebApplication.CreateBuilder(args);

        // Run happily as a Windows service when launched by the SCM (via
        // `dfdb service install`); a no-op for normal console `serve`.
        builder.Host.UseWindowsService(options => options.ServiceName = "DocumentForge");

        // Sensible default logging - quiet the per-request ASP.NET Core chatter
        // (4-5 lines per request swamps real signal at any traffic) but keep the
        // single 'Request finished ...' summary line. Override via env vars or
        // appsettings.json the standard ASP.NET way:
        //   Logging__LogLevel__Microsoft.AspNetCore=Information   (re-enable noise)
        //   Logging__LogLevel__Microsoft.AspNetCore=None          (silence entirely)
        builder.Logging
            .AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", Microsoft.Extensions.Logging.LogLevel.Information);

        var scheme = config.Security?.Tls is not null ? "https" : "http";
        var host = config.BindAllInterfaces ? "0.0.0.0" : "localhost";
        var bindUrl = $"{scheme}://{host}:{config.Port}";
        builder.WebHost.UseUrls(bindUrl);

        if (config.Security?.Tls is { } tls)
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ConfigureHttpsDefaults(https =>
                {
                    var pwd = tls.ResolveCertPassword();
                    https.ServerCertificate = pwd is null
                        ? new System.Security.Cryptography.X509Certificates.X509Certificate2(tls.CertPath)
                        : new System.Security.Cryptography.X509Certificates.X509Certificate2(tls.CertPath, pwd);
                });
            });
        }

        // Issue #101 — CORS is scoped by auth state. With no keys anywhere
        // (dev mode) every request is implicitly admin, so an allow-any
        // policy would let any website script admin actions against a
        // local node from the victim's browser. Dev mode therefore only
        // accepts loopback origins; once auth is on, bearer tokens make
        // cross-origin requests safe (browsers never attach them
        // implicitly) and any origin is accepted. The probe is assigned
        // after the key store exists; until then we stay restrictive.
        Func<bool>? devModeProbe = null;
        builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
            p.SetIsOriginAllowed(origin =>
                    !(devModeProbe?.Invoke() ?? true) || IsLoopbackOrigin(origin))
             .AllowAnyHeader().AllowAnyMethod()));
        var app = builder.Build();
        app.UseCors();

        Directory.CreateDirectory(config.DataDir);
        var dbPath = Path.Combine(config.DataDir, "data.dfdb");

        // Issue #66 Phase 1: every database now lives inside a registry, so
        // future phases (Studio "+" button, /databases CRUD, lazy open) can
        // grow without touching the route map. For Phase 1 the registry
        // holds exactly one entry — "default" — pointed at data.dfdb. All
        // existing flat routes resolve through registry.GetDefault(), so
        // single-DB clients written before this change see zero difference.
        var registry = new DatabaseRegistry();
        var db = registry.AttachOrCreate("default", dbPath);

        // Issue #73: auto-attach the _system DB alongside default. Holds
        // managed keys, audit log (future), runtime config overrides
        // (future). Treated as undeletable; hidden from /databases
        // listings so it doesn't clutter the Studio sidebar.
        var systemDbPath = Path.Combine(config.DataDir, "_system.dfdb");
        registry.AttachOrCreate("_system", systemDbPath);
        var keyStore = new Auth.KeyStore(registry);
        keyStore.Reload();

        // Issue #101 — deny by default. A node with no authentication
        // anywhere refuses to start unless the operator explicitly opts
        // in with --insecure-dev-mode, and even then only on loopback.
        // Binding all interfaces without auth is never allowed.
        var startupSecurityError = ValidateStartupSecurity(config, keyStore.Count);
        if (startupSecurityError is not null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  \x1b[31mERROR:\x1b[0m {startupSecurityError}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  To start this node, do one of the following:");
            Console.Error.WriteLine("    --api-key <key>          set an admin key (or DFDB_API_KEY, or security.apiKey in node.json)");
            Console.Error.WriteLine("    security.scopedKeys      define scoped keys in node.json");
            Console.Error.WriteLine("    --insecure-dev-mode      run WITHOUT auth — local development only, loopback only");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Generate a key with: openssl rand -hex 24");
            Console.Error.WriteLine();
            return 1;
        }

        // Issue #82 — persistent attach registry. Before this, any DB
        // attached via POST /databases lived in process memory only;
        // a restart dropped it. The catalog reads _system.databases at
        // boot AND auto-discovers any *.dfdb files in the data-dir
        // that aren't already attached, then keeps both in sync on
        // every Attach/Detach/Drop call.
        var dbCatalog = new DatabaseCatalog(registry);
        var bootstrap = dbCatalog.Bootstrap(config.DataDir);

        // Issue #87 — backup + recovery. Reads/writes settings to
        // _system.backup_config and audit rows to _system.backups.
        // Default backup dir is {data-dir}/backups; operators can
        // override via Studio's Backups settings panel.
        var backupManager = new BackupManager(registry, config.DataDir);

        // Issue #88 phase 1 — continuous WAL archiver. Tails each
        // enabled database's .recovery sidecar and ships new bytes as
        // timestamped segments. Restore-with-target (next phase)
        // concatenates the relevant segments into a synthetic
        // .recovery next to a restored snapshot and lets the engine's
        // existing replay path do the rest. Per-DB enable persists in
        // _system.wal_archive_state so PITR resumes after restart.
        var walArchiver = new WalArchiver(registry, backupManager);
        walArchiver.RestoreFromPersistedState();

        // Issue #88 phase 2 — point-in-time restore engine. Combines a
        // base snapshot from BackupManager with archived WAL segments
        // from WalArchiver, materialised through the engine's existing
        // recovery-log replay path on Open. No new replay code in the
        // engine — pure plumbing.
        var pitr = new PointInTimeRestore(registry, backupManager, walArchiver, config.DataDir);
        if (bootstrap.Errors.Count > 0)
        {
            foreach (var err in bootstrap.Errors)
                Console.Error.WriteLine($"[DocumentForge] WARNING: bootstrap attach failed — {err}");
        }

        // Optional replication wiring - leader / follower / none
        var replicationSummary = StartReplication(db, config);

        // Bearer-token auth middleware (Issue #72 — scoped keys).
        // Always installed; what it ENFORCES is a function of what's
        // configured:
        //   - No ApiKey, no ScopedKeys → dev mode. Every request gets
        //     an admin AuthContext. Effectively unauthenticated.
        //   - ApiKey only → existing single-key behaviour. Token must
        //     match. On match → admin AuthContext.
        //   - ScopedKeys only → token must match one of them. Each
        //     key's scopes define its access.
        //   - Both → admin key always works; scoped keys also work.
        //
        // /health stays public for load balancers.
        // OPTIONS (CORS preflight) stays public for browsers.
        //
        // Endpoints downstream consult HttpContext.GetAuth() to make
        // per-DB / admin / read-vs-write decisions.
        var adminKey = config.Security?.ApiKey;
        var scopedKeys = config.Security?.ScopedKeys ?? new List<ScopedApiKey>();
        // Dev mode = nothing in node.json AND nothing in the _system
        // DB. Computed lazily per request so that adding the first key
        // via POST /admin/keys *immediately* closes the dev-mode door
        // without restart. (Equivalent to SQL Server's "mixed auth"
        // tipping into real auth the moment a sa login exists.)
        Func<bool> isDevMode = () =>
            string.IsNullOrEmpty(adminKey) && scopedKeys.Count == 0 && keyStore.Count == 0;
        devModeProbe = isDevMode;
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Method == "OPTIONS") { await next(); return; }

            var path = ctx.Request.Path.Value ?? "";
            if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Items[Auth.AuthContext.ContextKey] = Auth.AuthContext.DevMode();
                await next();
                return;
            }

            if (isDevMode())
            {
                ctx.Items[Auth.AuthContext.ContextKey] = Auth.AuthContext.DevMode();
                await next();
                return;
            }

            var header = ctx.Request.Headers["Authorization"].ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                await Reject401(ctx);
                return;
            }
            var token = header.Substring(7);

            // Admin key match (back-compat). Wins regardless of scopes.
            if (!string.IsNullOrEmpty(adminKey) &&
                Auth.AuthContextExtensions.ConstantTimeEquals(token, adminKey))
            {
                ctx.Items[Auth.AuthContext.ContextKey] = Auth.AuthContext.FromScopes(new[] { "*" }, "admin");
                await next();
                return;
            }

            // Issue #73: persistent scoped keys. O(1) hash lookup against
            // the KeyStore's in-memory cache; no DB round-trip per request.
            var fromStore = keyStore.TryAuthenticate(token);
            if (fromStore is not null)
            {
                ctx.Items[Auth.AuthContext.ContextKey] = fromStore;
                await next();
                return;
            }

            // Config-file scoped keys (back-compat for node.json users).
            // Iterates because the list is tiny + constant-time per-key
            // comparison removes timing side-channels.
            foreach (var sk in scopedKeys)
            {
                if (string.IsNullOrEmpty(sk.Key)) continue;
                if (Auth.AuthContextExtensions.ConstantTimeEquals(token, sk.Key))
                {
                    var label = !string.IsNullOrEmpty(sk.Description) ? sk.Description : "scoped-key";
                    ctx.Items[Auth.AuthContext.ContextKey] = Auth.AuthContext.FromScopes(sk.Scopes, label);
                    await next();
                    return;
                }
            }

            await Reject401(ctx);
        });

        // Issue #72 — second-line enforcement for flat data-plane routes
        // that don't (yet) carry inline scope checks. A scoped key that
        // doesn't cover the *default* DB must not be able to touch
        // /collections/*, /query, /index, /seed, /tx/* — those all act
        // on the default DB. Admin tokens pass through untouched.
        //
        // This is path-prefix gating, intentionally coarse. Individual
        // endpoints that have already called ScopeCheck.RequireDbX still
        // get checked there too (defence in depth). Endpoint-level
        // checks will eventually replace this middleware once every
        // flat route has been audited.
        app.Use(async (ctx, next) =>
        {
            var auth = ctx.Items.TryGetValue(Auth.AuthContext.ContextKey, out var raw)
                && raw is Auth.AuthContext a ? a : Auth.AuthContext.Anonymous();
            if (auth.CanAdmin) { await next(); return; }

            var path = ctx.Request.Path.Value ?? "";
            // Flat data-plane prefixes targeting the default DB.
            // Anything under /db/{name}/* runs its own scope check;
            // anything under /databases / /services / /routers / /admin
            // already has admin gates at endpoint level.
            var needsDefaultDbScope =
                path.StartsWith("/collections", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/query", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/index", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/seed", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/tx/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/indexes/", StringComparison.OrdinalIgnoreCase);
            if (!needsDefaultDbScope) { await next(); return; }

            // Which DB? Path-route /db/{n}/... never gets here (covered
            // above). For flat routes resolve via header/query or fall
            // back to the registry's current default.
            var queryDb = ctx.Request.Query["database"].ToString();
            var headerDb = ctx.Request.Headers["X-Database"].ToString();
            var targetDb = !string.IsNullOrEmpty(queryDb) ? queryDb
                : !string.IsNullOrEmpty(headerDb) ? headerDb
                : registry.DefaultDatabaseName;
            if (string.IsNullOrEmpty(targetDb)) { await next(); return; }

            // Reads vs writes: we'd need to inspect the route to know.
            // Coarse rule: GET/HEAD are reads, everything else is a write.
            // True per-endpoint check still lives in the handler.
            var isWrite = ctx.Request.Method != "GET" && ctx.Request.Method != "HEAD";
            var ok = isWrite ? auth.CanWriteDb(targetDb) : auth.CanReadDb(targetDb);
            if (!ok)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = $"Forbidden — Bearer token lacks {(isWrite ? "write" : "read")} scope on db:{targetDb}.",
                    matchedKey = auth.MatchedKeyLabel,
                });
                return;
            }
            await next();
        });

        static async Task Reject401(HttpContext ctx)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Unauthorized. Provide Authorization: Bearer <apiKey>.",
            });
        }

        // Out-of-line blob store (issues #109/#71) — one per database, opened
        // lazily beside each data file. Blob bytes never enter the data file,
        // page cache, or WAL.
        var blobs = new Blobs.BlobStoreManager();

        // Blob replication (#109): a follower configured with an upstream URL
        // lazily pulls missing blob bytes from the leader by content hash the
        // first time a document's blob is read. Content-addressing makes the
        // pull self-verifying. Set via --blob-upstream-url or DFDB_BLOB_UPSTREAM_URL.
        var blobUpstream = GetArg(args, "--blob-upstream-url")
            ?? Environment.GetEnvironmentVariable("DFDB_BLOB_UPSTREAM_URL");
        if (!string.IsNullOrWhiteSpace(blobUpstream))
        {
            var upstreamBase = blobUpstream.TrimEnd('/');
            var blobHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) }; // large media pulls
            blobs.Upstream = async (hash, ct) =>
            {
                var resp = await blobHttp.GetAsync($"{upstreamBase}/blobs/raw/{hash}", HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStreamAsync(ct);
            };
        }

        PrintBanner(config, bindUrl, replicationSummary);
        MapEndpoints(app, db, registry, blobs);
        MapAdminEndpoints(app, db, config);
        MapReplicationEndpoints(app, db, config);
        // Issue #66 Phase 2: multi-database admin verbs (list/attach/create/
        // detach/drop/set-default). The registry holds the engines; flat
        // data-plane routes still resolve to registry.GetDefault() so
        // single-DB clients see no change. The catalog argument (Issue
        // #82) means attach/detach state survives restarts.
        DatabaseEndpoints.Map(app, registry, config.DataDir, dbCatalog, backupManager, walArchiver, pitr, blobs);

        // Issue #66 Phase 5: service orchestration. Lets Studio (or a CLI)
        // spawn sibling dfdb serve processes from one running service —
        // the foundation for the "create a local cluster in one click"
        // UX. Children are tracked, reaped on parent shutdown.
        var serviceManager = new ServiceManager();
        ServiceEndpoints.Map(app, serviceManager, config.DataDir, config);

        // Issue #73: managed (persisted) API keys. CRUD endpoints under
        // /admin/keys — all admin-scoped, backed by _system._keys via
        // the KeyStore set up above.
        KeysEndpoints.Map(app, keyStore);

        // Dispose the registry on shutdown — it cascades to every attached
        // database, including the Phase 1 single "default" one. The service
        // manager is disposed first so child processes are reaped before
        // we close their parent's data files.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { serviceManager.Dispose(); } catch { }
            try { blobs.Dispose(); } catch { }
            try { registry.Dispose(); } catch { }
        });
        app.Run();
        return 0;
    }

    /// <summary>Read the value following <paramref name="flag"/> in the CLI
    /// args, or null if the flag isn't present (or has no value after it).</summary>
    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void PrintBanner(NodeConfig config, string bindUrl, string replicationSummary)
    {
        Console.WriteLine();
        Console.WriteLine("  \x1b[36mdfdb serve\x1b[0m");
        Console.WriteLine($"  node:       {config.NodeName}");
        Console.WriteLine($"  data dir:   {Path.GetFullPath(config.DataDir)}");
        Console.WriteLine($"  listening:  {bindUrl}");
        Console.WriteLine($"  security:   API key {(string.IsNullOrEmpty(config.Security?.ApiKey) ? "\x1b[90mOFF (dev mode)\x1b[0m" : "\x1b[32mON\x1b[0m")}" +
                          $"  |  TLS {(config.Security?.Tls is null ? "\x1b[90mOFF\x1b[0m" : "\x1b[32mON\x1b[0m")}" +
                          $"  |  replication-secret {(string.IsNullOrEmpty(config.Security?.ReplicationSecret) ? "\x1b[90mOFF\x1b[0m" : "\x1b[32mON\x1b[0m")}");
        if (config.InsecureDevMode)
        {
            Console.WriteLine("  \x1b[31mWARNING:    --insecure-dev-mode — every request has FULL ADMIN access until a key is added.\x1b[0m");
            Console.WriteLine("  \x1b[31m            Loopback only. Never use this outside local development.\x1b[0m");
        }
        Console.WriteLine($"  replication: {replicationSummary}");
        Console.WriteLine();
        Console.WriteLine("  app API:   POST /query | GET /stats | GET /collections | POST /collections/{name}");
        Console.WriteLine("             POST /collections/{name}/bulk");
        Console.WriteLine("             GET|DELETE|PUT|PATCH /collections/{name}/{id}          (by internal _id)");
        Console.WriteLine("             GET|DELETE|PUT|PATCH /collections/{name}/by/{field}/{value} (by any field)");
        Console.WriteLine("             PATCH body: {field:val, x:null} merge  or  {\"$ops\":[{field,op,value}]}");
        Console.WriteLine("             DELETE /collections/{name} | GET /indexes/{collection} | POST /index");
        Console.WriteLine("             POST /tx/batch                  (atomic multi-doc transaction)");
        Console.WriteLine("             POST /seed | GET /health | GET /version");
        Console.WriteLine("  databases: GET  /databases | POST /databases | DELETE /databases/{name}");
        Console.WriteLine("             POST /databases/{name}/set-default");
        Console.WriteLine("  services:  GET  /services | POST /services | DELETE /services/{port}");
        Console.WriteLine("  keys:      GET  /admin/keys | POST /admin/keys | DELETE /admin/keys/{id}");
        Console.WriteLine("  admin:     POST /admin/flush | POST /admin/checkpoint | POST /admin/snapshot");
        Console.WriteLine("             POST /admin/compact/{collection}");
        Console.WriteLine("             POST /admin/rebuild-indexes/{collection}");
        Console.WriteLine("             POST /admin/rebuild-index/{collection}/{indexName}");
        Console.WriteLine("  replication:");
        Console.WriteLine("             GET  /replication/status");
        Console.WriteLine("             POST /replication/start-leader | /replication/start-follower");
        Console.WriteLine("             POST /replication/promote | /replication/read-only | /replication/read-write");
        Console.WriteLine("             POST /replication/auto-failover/enable | /disable");
        Console.WriteLine();
        Console.WriteLine($"  Manage with DocumentForge Studio — add an HTTP connection to {bindUrl}");
        Console.WriteLine();
    }

    /// <summary>
    /// Stands up the replication listener / follower loop based on config.Replication.
    /// Returns a short human-readable summary for the startup banner.
    /// </summary>
    private static string StartReplication(DocumentForgeDb db, NodeConfig config)
    {
        var rep = config.Replication;
        var secret = config.Security?.ReplicationSecret;

        if (rep is null || string.IsNullOrEmpty(rep.Role))
            return "\x1b[90mOFF (single node)\x1b[0m";

        if (rep.IsLeader)
        {
            var port = rep.Port ?? 5500;
            if (port == config.Port)
                throw new InvalidOperationException(
                    $"Replication port ({port}) must differ from the HTTP port ({config.Port}).");

            db.StartLogicalReplicationServer(port, secret);

            // Issue #95 — semi-sync: gate write acks on follower ACKs when configured.
            if (rep.MinSyncReplicas > 0)
            {
                db.MinSyncReplicas = rep.MinSyncReplicas;
                if (rep.SyncTimeoutSeconds > 0)
                    db.SyncReplicationTimeout = TimeSpan.FromSeconds(rep.SyncTimeoutSeconds);
            }

            return $"\x1b[32mLEADER\x1b[0m  listening on :{port}" +
                   (string.IsNullOrEmpty(secret) ? "" : "  (shared-secret required)") +
                   (rep.MinSyncReplicas > 0 ? $"  (semi-sync: {rep.MinSyncReplicas} replica ack)" : "");
        }

        if (rep.IsFollower)
        {
            if (string.IsNullOrEmpty(rep.LeaderHost) || rep.LeaderPort is null)
                throw new InvalidOperationException(
                    "Follower role requires --leader-host and --leader-port (or the matching node.json fields).");

            // Issue #51 — advertise our own HTTP base URL so the leader can
            // expose it in /replication/status. Falls back to a derived
            // host:port pair when Network.PublicBaseUrl isn't set.
            db.StartLogicalReplicationFollower(rep.LeaderHost!, rep.LeaderPort!.Value, secret, ownHttpEndpoint: config.ResolveHttpEndpoint());

            var detail = $"following {rep.LeaderHost}:{rep.LeaderPort}";

            if (rep.AutoFailover?.SilenceSeconds is int silenceSeconds && silenceSeconds > 0)
            {
                var newPort = rep.AutoFailover.NewLeaderPort ?? rep.LeaderPort!.Value;
                db.EnableAutoFailover(
                    newLeaderPort: newPort,
                    silenceTimeout: TimeSpan.FromSeconds(silenceSeconds),
                    onPromoted: p => Console.WriteLine($"[dfdb] auto-failover: promoted to leader on :{p}"));
                detail += $"  |  auto-failover after {silenceSeconds}s (new port :{newPort})";
            }

            return $"\x1b[32mFOLLOWER\x1b[0m  {detail}";
        }

        throw new InvalidOperationException(
            $"Unknown replication role '{rep.Role}'. Expected 'leader' or 'follower'.");
    }

    private static void MapEndpoints(WebApplication app, DocumentForgeDb db, DatabaseRegistry registry, Blobs.BlobStoreManager blobs)
    {
        // Issues #109/#71 — out-of-line blob storage on a document field. Bytes
        // stream to a content-addressed sibling store, never the data file; the
        // field ends up holding a small { "$blob": ... } descriptor.
        app.MapPut("/collections/{name}/{id}/blob/{field}", (string name, string id, string field, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return Task.FromResult(InvalidCollectionNameResult());
            return Blobs.BlobHandler.Upload(db, blobs.For(db), name, id, field, request);
        });

        app.MapGet("/collections/{name}/{id}/blob/{field}", (string name, string id, string field, HttpRequest request, HttpResponse response) =>
        {
            if (!IsValidCollectionName(name)) return Task.FromResult(InvalidCollectionNameResult());
            return Blobs.BlobHandler.Download(db, blobs, name, id, field, request, response);
        });

        app.MapDelete("/collections/{name}/{id}/blob/{field}", (string name, string id, string field, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            return Blobs.BlobHandler.Remove(db, name, id, field, request);
        });

        // Issue #71 — "attachment" alias for the blob routes (the attachment key
        // IS the document field). POST accepts a Content-Range header for
        // resumable/chunked upload; GET streams with Range; DELETE is X-Confirm
        // guarded. These give media apps the exact attachments API they asked
        // for over the same content-addressed, out-of-line store.
        app.MapPost("/collections/{name}/{id}/attachments/{key}", (string name, string id, string key, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return Task.FromResult(InvalidCollectionNameResult());
            return Blobs.BlobHandler.Upload(db, blobs.For(db), name, id, key, request);
        });
        app.MapPut("/collections/{name}/{id}/attachments/{key}", (string name, string id, string key, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return Task.FromResult(InvalidCollectionNameResult());
            return Blobs.BlobHandler.Upload(db, blobs.For(db), name, id, key, request);
        });
        app.MapGet("/collections/{name}/{id}/attachments/{key}", (string name, string id, string key, HttpRequest request, HttpResponse response) =>
        {
            if (!IsValidCollectionName(name)) return Task.FromResult(InvalidCollectionNameResult());
            return Blobs.BlobHandler.Download(db, blobs, name, id, key, request, response);
        });
        app.MapDelete("/collections/{name}/{id}/attachments/{key}", (string name, string id, string key, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            return Blobs.BlobHandler.Remove(db, name, id, key, request);
        });

        // Out-of-band mark-sweep GC: reclaim blobs no document references.
        app.MapPost("/admin/blobs/gc", () =>
        {
            var referenced = Blobs.BlobStoreManager.CollectReferencedBlobIds(db);
            var store = blobs.For(db);
            var collected = store.GarbageCollect(referenced);
            return Results.Ok(new { success = true, referenced = referenced.Count, collected, liveBytes = store.LiveBytes });
        });

        app.MapGet("/blobs/stats", () =>
        {
            var store = blobs.For(db);
            return Results.Ok(new { count = store.Count, liveBytes = store.LiveBytes });
        });

        // Reclaim disk from GC'd blobs: rewrite the store with only live bytes.
        app.MapPost("/admin/blobs/compact", () =>
        {
            var r = blobs.For(db).Compact();
            return Results.Ok(new { success = true, liveBlobs = r.LiveBlobs, droppedBlobs = r.DroppedBlobs, bytesReclaimed = r.BytesReclaimed });
        });

        // Content-addressed raw fetch by SHA-256 — the source a follower pulls
        // from for lazy blob replication (#109). No document lookup; 404 if the
        // hash isn't stored here. Honours Range so a follower can stream large
        // blobs. The id is a 64-char hex hash, validated to avoid path tricks.
        app.MapGet("/blobs/raw/{sha256}", (string sha256, HttpResponse response) =>
        {
            if (!Blobs.BlobHandler.IsValidBlobId(sha256))
                return Results.BadRequest(new { error = "Blob id must be a 64-char SHA-256 hex string." });
            var stream = blobs.For(db).OpenRead(sha256);
            if (stream is null) return Results.NotFound(new { error = "Blob not stored here." });
            response.Headers["Accept-Ranges"] = "bytes";
            return Results.Stream(stream, "application/octet-stream");
        });

        // POST /query - materialised JSON response (default, back-compat).
        // Pass ?stream=true OR Accept: application/x-ndjson for an NDJSON stream:
        // first line is the metadata envelope, then one document per line.
        app.MapPost("/query", async (QueryRequest request, HttpRequest httpReq, HttpResponse httpRes, bool? stream) =>
        {
            if (string.IsNullOrWhiteSpace(request.Sql))
                return Results.BadRequest(new { error = "Missing 'sql' field" });

            var wantsStream = stream == true
                || httpReq.Headers.Accept.ToString().Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase);

            var result = db.Execute(request.Sql);
            // `code` is a stable machine-readable identifier (issue #69) —
            // e.g. "collectionNotFound" when reading a collection that has
            // not been lazily created yet. Clients branch on it instead of
            // string-matching the human message.
            if (!result.Success) return Results.BadRequest(new { error = result.Message, code = result.ErrorCode });

            if (!wantsStream)
            {
                var docs = result.Documents.Select(d => JsonDocument.Parse(d.ToJson()).RootElement).ToList();
                return Results.Ok(new
                {
                    success = true, count = result.Documents.Count, affected = result.AffectedCount,
                    plan = result.QueryPlan, executionTimeMs = result.ExecutionTime.TotalMilliseconds,
                    message = result.Message, documents = docs
                });
            }

            // NDJSON: line 1 = envelope, lines 2..N = one doc per line.
            httpRes.ContentType = "application/x-ndjson";
            httpRes.Headers["X-DFDB-Plan"] = result.QueryPlan;
            httpRes.Headers["X-DFDB-Count"] = result.Documents.Count.ToString();
            httpRes.Headers["X-DFDB-ExecutionMs"] = result.ExecutionTime.TotalMilliseconds.ToString("F2");

            var envelope = JsonSerializer.Serialize(new
            {
                kind = "meta",
                count = result.Documents.Count,
                affected = result.AffectedCount,
                plan = result.QueryPlan,
                executionTimeMs = result.ExecutionTime.TotalMilliseconds,
                message = result.Message
            });
            await httpRes.WriteAsync(envelope);
            await httpRes.WriteAsync("\n");
            await httpRes.Body.FlushAsync();

            foreach (var doc in result.Documents)
            {
                await httpRes.WriteAsync(doc.ToJson());
                await httpRes.WriteAsync("\n");
            }
            await httpRes.Body.FlushAsync();
            return Results.Empty;
        });

        // Issue #72 — consistent DB selection on flat /collections.
        // Honour ?database=<name> or X-Database header so callers can
        // target any attached DB without switching to the scoped
        // /db/{name}/collections path. Falls back to the default DB
        // when neither is supplied.
        app.MapGet("/collections", (HttpContext httpCtx, string? database) =>
        {
            var requestedDb = ResolveTargetDatabase(httpCtx, database);
            var targetDb = requestedDb is null ? db : registry.TryGet(requestedDb);
            if (targetDb is null)
                return Results.NotFound(new { error = $"Database '{requestedDb}' is not attached." });
            var resolvedName = requestedDb ?? registry.DefaultDatabaseName ?? "default";
            if (ScopeCheck.RequireDbRead(httpCtx, resolvedName) is { } deny) return deny;
            return Results.Ok(new
            {
                database = resolvedName,
                collections = targetDb.GetCollectionNames(),
            });
        });

        app.MapPost("/collections/{name}", async (string name, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });
            try
            {
                var id = db.Insert(name, json);
                return Results.Created($"/collections/{name}/{id}", new { success = true, id = id.ToString(), collection = name });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/collections/{name}", (string name, int? limit) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            var sql = $"SELECT * FROM {name}";
            if (limit.HasValue) sql += $" LIMIT {limit.Value}";
            var result = db.Execute(sql);
            var docs = result.Documents.Select(d => JsonDocument.Parse(d.ToJson()).RootElement).ToList();
            return Results.Ok(new { collection = name, count = docs.Count, documents = docs });
        });

        app.MapDelete("/collections/{name}/{id}", (string name, string id) =>
        {
            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id. To delete by a business key, use DELETE /collections/{name}/by/{field}/{value}." });
            var docId = new DocumentId(guid);
            var doc = coll.FindById(docId);
            if (doc is null) return Results.NotFound();
            if (coll.Delete(docId)) db.NotifyDocDeleted(name, docId, doc);
            return Results.Ok(new { success = true });
        });

        // Replace a document by internal _id. Body is the full new document
        // (the original _id is always preserved — you don't need to include it).
        //
        // Issue #18 — optimistic concurrency: if the request carries an
        // `If-Match: <etag>` header, ReplaceIfEtag is used and a mismatch
        // returns 412 Precondition Failed with the current ETag in both
        // the response header and body. Without the header it's
        // last-write-wins (back-compat with pre-#18 callers).
        app.MapPut("/collections/{name}/{id}", async (string name, string id, HttpRequest request) =>
        {
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id. To update by a business key, use PUT /collections/{name}/by/{field}/{value}." });

            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });

            try
            {
                var docId = new DocumentId(guid);
                // ETag passed via the standard `If-Match` HTTP header. We
                // accept both quoted (`"<etag>"`, RFC 9110 strict) and
                // unquoted forms — clients hand-rolling the header often
                // forget the quotes, and for an opaque-string token there's
                // no semantic difference.
                var ifMatch = NormaliseIfMatch(request.Headers["If-Match"].ToString());

                if (!string.IsNullOrEmpty(ifMatch))
                {
                    string? newEtag;
                    try { newEtag = db.ReplaceIfEtag(name, docId, json, ifMatch); }
                    catch (EtagMismatchException ex)
                    {
                        return Results.Json(new
                        {
                            error = ex.Message,
                            expected = ex.ExpectedEtag,
                            actual = ex.ActualEtag,
                        }, statusCode: StatusCodes.Status412PreconditionFailed);
                    }
                    if (newEtag is null) return Results.NotFound();
                    return Results.Ok(new
                    {
                        success = true,
                        id = docId.ToString(),
                        collection = name,
                        etag = newEtag,
                    });
                }

                // Last-write-wins path.
                var ok = db.Replace(name, docId, json);
                if (!ok) return Results.NotFound();
                return Results.Ok(new { success = true, id = docId.ToString(), collection = name });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Issue #103 — atomic conditional update / compare-and-set. Applies the
        // operations only if every condition holds, all under one write lock,
        // so racing callers can't both win (e.g. "decrement seats if >= 1").
        // 200 on success, 409 when a condition fails, 404 not found.
        app.MapPost("/collections/{name}/{id}/conditional", (string name, string id, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return Task.FromResult(InvalidCollectionNameResult());
            return ConditionalUpdateHandler.Handle(db, name, id, request);
        });

        // Issue #70 — partial-document update (PATCH). Applies a shallow JSON
        // merge (fields to set; null removes) or an explicit { "$ops": [...] }
        // list, atomically under the write lock — no client read-modify-write.
        // Optional If-Match header pins the update to a specific _etag (412 on
        // mismatch). 200 on success, 404 not found.
        app.MapPatch("/collections/{name}/{id}", (string name, string id, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return Task.FromResult(InvalidCollectionNameResult());
            return PatchHandler.Handle(db, name, id, request);
        });

        // PATCH by business key — resolves {field}={value} to an _id (honouring
        // indexes) then applies the same merge/$ops patch.
        app.MapPatch("/collections/{name}/by/{field}/{value}", async (string name, string field, string value, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            if (!IsValidFieldPath(field))
                return Results.BadRequest(new { error = "Field name must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });

            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();

            var safeValue = value.Replace("'", "''");
            var found = db.Execute($"SELECT * FROM {name} WHERE {field} = '{safeValue}' LIMIT 1");
            if (!found.Success) return Results.BadRequest(new { error = found.Message, code = found.ErrorCode });
            if (found.Documents.Count == 0) return Results.NotFound();

            var docId = found.Documents[0].GetId();
            return await PatchHandler.Handle(db, name, docId.ToString(), request);
        });

        // Replace by business key. Finds the first document where {field} = {value}
        // and replaces its content with the request body. Field name and value are
        // sanitised the same way as the GET/DELETE by-field routes.
        app.MapPut("/collections/{name}/by/{field}/{value}", async (string name, string field, string value, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            if (!IsValidFieldPath(field))
                return Results.BadRequest(new { error = "Field name must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });

            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });

            // Missing collection → 404, matching the GET/DELETE by-field
            // routes (issue #69: this used to surface as a 400 from the
            // SQL lookup below).
            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();

            // Look up the doc id via SQL so indexes are honoured.
            var safeValue = value.Replace("'", "''");
            var lookupSql = $"SELECT * FROM {name} WHERE {field} = '{safeValue}' LIMIT 1";
            var found = db.Execute(lookupSql);
            if (!found.Success) return Results.BadRequest(new { error = found.Message, code = found.ErrorCode });
            if (found.Documents.Count == 0) return Results.NotFound();

            try
            {
                var docId = found.Documents[0].GetId();
                var ok = db.Replace(name, docId, json);
                if (!ok) return Results.NotFound();
                return Results.Ok(new
                {
                    success = true,
                    id = docId.ToString(),
                    collection = name,
                    matchedBy = new { field, value },
                    plan = found.QueryPlan
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Find a single document by id. Issue #18: returns the current ETag
        // in an `ETag:` response header so clients can store it and use it
        // in a subsequent If-Match PUT.
        app.MapGet("/collections/{name}/{id}", (string name, string id, HttpResponse response) =>
        {
            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id (a Guid-formatted 16-byte value returned from POST /collections/{name}). To look up by your own business key, use GET /collections/{name}/by/{field}/{value}." });
            var doc = coll.FindById(new DocumentId(guid));
            if (doc is null) return Results.NotFound();

            var etag = doc.GetEtag();
            if (!string.IsNullOrEmpty(etag))
                response.Headers.ETag = $"\"{etag}\"";

            return Results.Ok(JsonDocument.Parse(doc.ToJson()).RootElement);
        });

        // Look up by any field - e.g. GET /collections/orders/by/pnr/ABC123
        // Uses an index when one exists on the field; falls back to collection scan otherwise.
        // The field name is validated against a whitelist to prevent SQL injection;
        // the value is escaped by doubling single quotes.
        app.MapGet("/collections/{name}/by/{field}/{value}", (string name, string field, string value) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            if (!IsValidFieldPath(field))
                return Results.BadRequest(new { error = "Field name must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });

            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();

            var safeValue = value.Replace("'", "''");
            var sql = $"SELECT * FROM {name} WHERE {field} = '{safeValue}' LIMIT 1";
            var r = db.Execute(sql);
            if (!r.Success) return Results.BadRequest(new { error = r.Message, code = r.ErrorCode });
            if (r.Documents.Count == 0) return Results.NotFound();

            return Results.Ok(new
            {
                collection = name,
                plan = r.QueryPlan,
                executionTimeMs = r.ExecutionTime.TotalMilliseconds,
                document = JsonDocument.Parse(r.Documents[0].ToJson()).RootElement
            });
        });

        // Delete by any field - same semantics as the GET-by-field above.
        // Deletes at most the matching documents (no LIMIT clause means all matches).
        app.MapDelete("/collections/{name}/by/{field}/{value}", (string name, string field, string value) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            if (!IsValidFieldPath(field))
                return Results.BadRequest(new { error = "Field name must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });

            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();

            var safeValue = value.Replace("'", "''");
            var sql = $"DELETE FROM {name} WHERE {field} = '{safeValue}'";
            var r = db.Execute(sql);
            if (!r.Success) return Results.BadRequest(new { error = r.Message, code = r.ErrorCode });

            return Results.Ok(new
            {
                success = true,
                collection = name,
                deletedCount = r.AffectedCount,
                plan = r.QueryPlan,
                executionTimeMs = r.ExecutionTime.TotalMilliseconds
            });
        });

        // Bulk insert: accepts a JSON array of documents.
        //
        // Response shape:
        //   {
        //     success: bool,         // true iff every doc landed (and !rolledBack)
        //     count: N,              // number of inserted ids
        //     ids: [docId, ...],     // _id strings, in input order, of every inserted doc
        //     errors: [{ index, error }, ...],   // per-doc failures (omitted if atomic+success)
        //     rolledBack: bool,      // true iff atomic=true and we rolled back on first failure
        //     atomic: bool, indexesRebuilt: N, indexesSkipped: bool, timeSeconds: f
        //   }
        //
        // Query flags:
        //   ?atomic=true       all-or-nothing - first error rolls back every previous insert
        //                      in the same lock window. Returns 400 with rolledBack=true.
        //   ?skipIndexes=true  cold-load mode: don't rebuild indexes after the batch.
        //                      You then MUST call POST /admin/rebuild-indexes/{name}
        //                      before any indexed query, or results will be wrong.
        app.MapPost("/collections/{name}/bulk", async (string name, HttpRequest request, bool? skipIndexes, bool? atomic) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });

            List<BsonDocument>? docs;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Body must be a JSON array of documents." });

                docs = new List<BsonDocument>(parsed.RootElement.GetArrayLength());
                foreach (var el in parsed.RootElement.EnumerateArray())
                    docs.Add(BsonDocument.FromJson(el.GetRawText()));
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            var atomicMode = atomic == true;
            var skipped = skipIndexes == true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = db.BulkInsertTracked(name, docs, atomic: atomicMode);

            // Rebuild indexes after the batch unless explicitly asked not to.
            // Skip the rebuild on a rolled-back atomic batch (nothing inserted to index).
            var indexesRebuilt = 0;
            if (!skipped && !result.RolledBack)
            {
                var existingIndexes = db.GetIndexes(name);
                if (existingIndexes.Count > 0)
                {
                    db.RebuildIndexes(name);
                    indexesRebuilt = existingIndexes.Count;
                }
            }

            sw.Stop();

            var responseBody = new
            {
                success = result.Errors.Count == 0 && !result.RolledBack,
                count = result.InsertedIds.Count,
                ids = result.InsertedIds.Select(i => i.ToString()).ToArray(),
                errors = result.Errors.Select(e => new { index = e.Index, error = e.Error }).ToArray(),
                rolledBack = result.RolledBack,
                atomic = atomicMode,
                indexesRebuilt,
                indexesSkipped = skipped,
                timeSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3)
            };

            // Atomic mode that rolled back -> 400 (caller asked us to fail the whole thing).
            // Non-atomic with some errors -> still 200, the response carries the per-doc errors.
            return result.RolledBack
                ? Results.BadRequest(responseBody)
                : Results.Ok(responseBody);
        });

        // Drop an entire collection (destructive - requires explicit X-Confirm: true header)
        app.MapDelete("/collections/{name}", (string name, HttpRequest request) =>
        {
            if (request.Headers["X-Confirm"].ToString() != "true")
                return Results.BadRequest(new { error = "Destructive op. Include header 'X-Confirm: true' to proceed." });
            var dropped = db.DropCollection(name);
            return dropped
                ? Results.Ok(new { success = true, dropped = name })
                : Results.NotFound();
        });

        app.MapGet("/indexes/{collection}", (string collection) =>
        {
            var indexes = db.GetIndexes(collection);
            return Results.Ok(new { collection,
                indexes = indexes.Select(i => new { name = i.Name, path = i.JsonPath, unique = i.IsUnique }) });
        });

        app.MapGet("/stats", () =>
        {
            db.Flush();
            var stats = db.GetStatistics();
            return Results.Ok(new
            {
                filePath = stats.FilePath,
                fileSizeMb = Math.Round(stats.FileSize / 1024.0 / 1024.0, 2),
                pageCount = stats.PageCount,
                cachedPages = stats.CachedPages,
                dirtyPages = stats.DirtyPages,
                collections = stats.Collections.Select(c => new
                {
                    name = c.Name,
                    documentCount = c.DocumentCount,
                    indexes = c.Indexes.Select(i => new
                    {
                        name = i.Name, path = i.JsonPath, entries = i.EntryCount, unique = i.IsUnique
                    })
                })
            });
        });

        app.MapPost("/index", (CreateIndexRequest request) =>
        {
            try
            {
                db.CreateIndex(request.Collection, request.Path, request.Name, request.Unique);
                var indexes = db.GetIndexes(request.Collection);
                return Results.Ok(new { success = true,
                    message = $"Index '{request.Name}' on {request.Collection}({request.Path})", totalIndexes = indexes.Count });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Issue #104 — TTL / expiry. Configure a rule so documents whose
        // {field} timestamp is at/before now get deleted by the background
        // sweep. The field is an ISO-8601 datetime or a Unix-epoch-millis
        // number. Body: { "field": "holdExpiry", "intervalSeconds": 30 }.
        app.MapPut("/collections/{name}/ttl", async (string name, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (!json.RootElement.TryGetProperty("field", out var f) || f.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(f.GetString()))
                    return Results.BadRequest(new { error = "Body must include a non-empty string 'field'." });
                TimeSpan? interval = null;
                if (json.RootElement.TryGetProperty("intervalSeconds", out var iv) && iv.ValueKind == JsonValueKind.Number)
                    interval = TimeSpan.FromSeconds(iv.GetDouble());
                db.ConfigureTtl(name, f.GetString()!, interval);
                return Results.Ok(new { success = true, collection = name, field = f.GetString(),
                    intervalSeconds = (interval ?? TtlConfig.DefaultSweepInterval).TotalSeconds });
            }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/collections/{name}/ttl", (string name) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            var removed = db.RemoveTtl(name);
            return removed ? Results.Ok(new { success = true, collection = name, removed = true })
                           : Results.NotFound(new { error = $"No TTL configured on '{name}'." });
        });

        app.MapGet("/ttl", () => Results.Ok(new
        {
            ttls = db.GetTtlConfigs().Select(t => new
            {
                collection = t.Collection, field = t.Field, intervalSeconds = t.SweepInterval.TotalSeconds
            })
        }));

        // Force an immediate expiry sweep (the timer also runs it periodically).
        app.MapPost("/admin/ttl/sweep", () =>
        {
            var deleted = db.SweepExpired();
            return Results.Ok(new { success = true, deleted });
        });

        // Issue #106 — opt-in per-collection schema / constraints. PUT a schema
        // to enforce required fields, field types, and CHECK conditions on
        // every subsequent write; DELETE to go back to schemaless.
        app.MapPut("/collections/{name}/schema", async (string name, HttpRequest request) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest(new { error = "Empty body." });
            try
            {
                using var json = JsonDocument.Parse(body);
                var schema = SchemaHandler.Parse(name, json.RootElement);
                db.ConfigureSchema(schema);
                return Results.Ok(new { success = true, schema = SchemaHandler.ToWire(schema) });
            }
            catch (SchemaHandler.SchemaParseException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/collections/{name}/schema", (string name) =>
        {
            if (!IsValidCollectionName(name)) return InvalidCollectionNameResult();
            return db.RemoveSchema(name)
                ? Results.Ok(new { success = true, collection = name, removed = true })
                : Results.NotFound(new { error = $"No schema configured on '{name}'." });
        });

        app.MapGet("/collections/{name}/schema", (string name) =>
        {
            var schema = db.GetSchema(name);
            return schema is null
                ? Results.NotFound(new { error = $"No schema configured on '{name}'." })
                : Results.Ok(SchemaHandler.ToWire(schema));
        });

        // Atomic multi-document transaction. Body is a JSON array of ops:
        //   { "op": "insert",        "collection": "users", "doc": {...} }
        //   { "op": "replace",       "collection": "users", "id": "<guid>", "doc": {...} }
        //   { "op": "delete",        "collection": "users", "id": "<guid>" }
        //   { "op": "deleteByField", "collection": "users", "field": "email", "value": "a@b.com" }
        // The whole batch commits or none of it does. A unique-index conflict
        // anywhere in the batch (including conflicts the batch itself creates)
        // returns 400 with the failing op index. Cross-shard transactions and
        // imperative session-style transactions are tracked separately as
        // Phase 2/3 work.
        app.MapPost("/tx/batch", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty body" });

            JsonDocument? parsed;
            try { parsed = JsonDocument.Parse(body); }
            catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

            using (parsed)
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Body must be a JSON array of ops." });

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var tx = db.BeginTransaction();
                var results = new List<object>();
                int opIndex = 0;

                try
                {
                    foreach (var el in parsed.RootElement.EnumerateArray())
                    {
                        var op = el.TryGetProperty("op", out var opEl) ? opEl.GetString() : null;
                        var collection = el.TryGetProperty("collection", out var cEl) ? cEl.GetString() : null;
                        if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(collection))
                        {
                            return Results.BadRequest(new { error = $"op[{opIndex}]: 'op' and 'collection' are required." });
                        }
                        if (!IsValidCollectionName(collection!))
                            return Results.BadRequest(new { error = $"op[{opIndex}]: collection name must match [a-zA-Z_][a-zA-Z0-9_]* and be at most {MaxCollectionNameLength} chars." });

                        switch (op)
                        {
                            case "insert":
                            {
                                if (!el.TryGetProperty("doc", out var docEl))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] insert: 'doc' is required." });
                                var doc = BsonDocument.FromJson(docEl.GetRawText());
                                var id = tx.Insert(collection!, doc);
                                results.Add(new { op, collection, id = id.ToString() });
                                break;
                            }
                            case "replace":
                            {
                                if (!el.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var rguid))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] replace: valid 'id' is required." });
                                if (!el.TryGetProperty("doc", out var docEl))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] replace: 'doc' is required." });
                                var rid = new DocumentId(rguid);
                                var doc = BsonDocument.FromJson(docEl.GetRawText());
                                var ok = tx.Replace(collection!, rid, doc);
                                if (!ok)
                                    return Results.BadRequest(new { error = $"op[{opIndex}] replace: id '{rid}' not found in {collection}." });
                                results.Add(new { op, collection, id = rid.ToString() });
                                break;
                            }
                            case "delete":
                            {
                                if (!el.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var dguid))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] delete: valid 'id' is required." });
                                var did = new DocumentId(dguid);
                                var ok = tx.Delete(collection!, did);
                                results.Add(new { op, collection, id = did.ToString(), deleted = ok });
                                break;
                            }
                            case "deleteByField":
                            {
                                var field = el.TryGetProperty("field", out var fEl) ? fEl.GetString() : null;
                                var value = el.TryGetProperty("value", out var vEl) ? vEl.GetString() : null;
                                if (string.IsNullOrEmpty(field) || value is null)
                                    return Results.BadRequest(new { error = $"op[{opIndex}] deleteByField: 'field' and 'value' are required." });
                                if (!IsValidFieldPath(field))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] deleteByField: 'field' must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });
                                int n = tx.DeleteByField(collection!, field, value);
                                results.Add(new { op, collection, field, value, deleted = n });
                                break;
                            }
                            default:
                                return Results.BadRequest(new { error = $"op[{opIndex}]: unknown op '{op}' (expected insert|replace|delete|deleteByField)." });
                        }
                        opIndex++;
                    }

                    tx.Commit();
                    sw.Stop();
                    return Results.Ok(new
                    {
                        success = true,
                        transactionId = tx.Id,
                        operationCount = results.Count,
                        results,
                        timeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3)
                    });
                }
                catch (DocumentForgeException ex)
                {
                    sw.Stop();
                    // tx is auto-rolled back on dispose since Commit didn't reach.
                    return Results.BadRequest(new
                    {
                        success = false,
                        transactionId = tx.Id,
                        failedOpIndex = opIndex,
                        error = ex.Message,
                        rolledBack = true,
                        timeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3)
                    });
                }
            }
        });

        app.MapPost("/seed", (SeedRequest? request) =>
        {
            int count = request?.Orders ?? 500;
            var rng = new Random(42);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                db.Insert("orders", DocumentForge.AirlineDemo.SeedData.GenerateOrder(rng, i));
            for (int i = 0; i < 100; i++)
            {
                string[] airlines = { "AA", "UA", "DL", "BA", "LH" };
                var al = airlines[rng.Next(airlines.Length)];
                var fn = $"{al}{rng.Next(100, 9999)}";
                var date = DateTime.Today.AddDays(rng.Next(0, 30)).ToString("yyyy-MM-dd");
                db.Insert("flights", DocumentForge.AirlineDemo.SeedData.GenerateFlight(rng, fn, date));
            }
            try { db.CreateIndex("orders", "pnr", "idx_orders_pnr", unique: true); } catch { }
            try { db.CreateIndex("orders", "passenger.lastName", "idx_orders_lastname"); } catch { }
            try { db.CreateIndex("orders", "status", "idx_orders_status"); } catch { }
            try { db.CreateIndex("flights", "flightNumber", "idx_flights_number"); } catch { }
            try { db.CreateIndex("flights", "departureAirport", "idx_flights_departure"); } catch { }
            sw.Stop();
            return Results.Ok(new { success = true, ordersSeeded = count, flightsSeeded = 100,
                indexesCreated = 5, timeSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2) });
        });
    }

    internal static void MapAdminEndpoints(WebApplication app, DocumentForgeDb db, NodeConfig config)
    {
        // Liveness + identity
        app.MapGet("/health", () =>
        {
            // Issue #25: surface engine-wide health status. A Failed engine
            // returns 503 so load balancers / orchestrators (Render, k8s,
            // Docker HEALTHCHECK) take it out of rotation while the operator
            // restarts it (which runs recovery-log replay).
            bool healthy = db.HealthStatus == DocumentForge.Core.DatabaseHealthStatus.Healthy;
            var body = new
            {
                status = healthy ? "ok" : "degraded",
                node = config.NodeName,
                version = "1.4.0",
                readOnly = db.IsReadOnly,
                uptimeSeconds = Math.Round((DateTime.UtcNow - _startedAt).TotalSeconds, 1),
                health = healthy ? null : new
                {
                    state = db.HealthStatus.ToString(),
                    lastFailure = db.LastHealthFailure?.Message,
                },
            };
            return healthy
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // Build-identification info — returns the SHA, build timestamp, and
        // (when running in a container) the image identifier so deploy
        // verifiers can confirm what's running. See BuildInfo.cs for the
        // resolution chain (assembly metadata → env vars → fallbacks). Issue #36.
        app.MapGet("/version", () => Results.Ok(new
        {
            sha = BuildInfo.Sha,
            builtAt = BuildInfo.BuiltAtUtc,
            image = BuildInfo.Image,
            node = config.NodeName,
        }));

        // Issue #111 — service settings API. GET returns the effective node
        // configuration with all secrets redacted to presence + a short
        // fingerprint (never the raw key / TLS password). Admin-scope gated.
        // The semi-sync knobs reflect the LIVE engine values, which PUT can
        // change without a restart.
        app.MapGet("/admin/config", (HttpContext httpCtx) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;
            return Results.Ok(BuildConfigView(db, config));
        });

        // PUT applies the live-editable subset only (currently the semi-sync
        // replication knobs, which live on the engine). Restart-required fields
        // (port, data dir, bind-all, TLS, replication role/topology, keys) are
        // rejected with a 400 that tells the operator to edit node.json and
        // restart — they are never silently ignored.
        app.MapPut("/admin/config", async (HttpContext httpCtx) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;

            using var reader = new StreamReader(httpCtx.Request.Body);
            var raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw))
                return Results.BadRequest(new { error = "Empty body. Send the live-editable fields to change (e.g. { \"minSyncReplicas\": 1 })." });

            JsonElement root;
            try { using var doc = JsonDocument.Parse(raw); root = doc.RootElement.Clone(); }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }
            if (root.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Body must be a JSON object." });

            // Guard: reject any restart-required field explicitly so an operator
            // never thinks a port change took effect when it didn't.
            foreach (var restart in RestartRequiredFields)
                if (root.TryGetProperty(restart, out _))
                    return Results.BadRequest(new
                    {
                        error = $"'{restart}' is restart-required and cannot be changed over the API. Edit node.json and restart the service.",
                        code = "restartRequired",
                        field = restart,
                    });

            var applied = new List<string>();
            try
            {
                if (root.TryGetProperty("minSyncReplicas", out var msr))
                {
                    if (msr.ValueKind != JsonValueKind.Number || !msr.TryGetInt32(out var v) || v < 0)
                        return Results.BadRequest(new { error = "'minSyncReplicas' must be a non-negative integer." });
                    db.MinSyncReplicas = v;
                    config.Replication ??= new ReplicationConfig();
                    config.Replication.MinSyncReplicas = v;
                    applied.Add("minSyncReplicas");
                }
                if (root.TryGetProperty("syncTimeoutSeconds", out var sts))
                {
                    if (sts.ValueKind != JsonValueKind.Number || !sts.TryGetDouble(out var s) || s <= 0)
                        return Results.BadRequest(new { error = "'syncTimeoutSeconds' must be a positive number." });
                    db.SyncReplicationTimeout = TimeSpan.FromSeconds(s);
                    config.Replication ??= new ReplicationConfig();
                    config.Replication.SyncTimeoutSeconds = s;
                    applied.Add("syncTimeoutSeconds");
                }
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            if (applied.Count == 0)
                return Results.BadRequest(new
                {
                    error = "No live-editable fields present. Editable now: minSyncReplicas, syncTimeoutSeconds.",
                    liveEditable = LiveEditableFields,
                });

            return Results.Ok(new { saved = true, applied, config = BuildConfigView(db, config) });
        });

        // Force a cache flush (all dirty pages to disk, truncate recovery log)
        app.MapPost("/admin/flush", (HttpContext httpCtx) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            db.Flush();
            sw.Stop();
            return Results.Ok(new { success = true, timeMs = sw.Elapsed.TotalMilliseconds });
        });

        // Synonym for flush - common DB ops term
        app.MapPost("/admin/checkpoint", (HttpContext httpCtx) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            db.Checkpoint();
            sw.Stop();
            return Results.Ok(new { success = true, timeMs = sw.Elapsed.TotalMilliseconds });
        });

        // Take a consistent snapshot of the data file. Blocks writes briefly
        // (the duration of FlushAll + the file copy) and returns when the
        // snapshot is durable. The result file at targetPath is a
        // self-contained .dfdb that DocumentForgeDb.Open can load directly.
        //
        // For multi-GB datasets the copy dominates wall time; consider
        // running this against a follower instead so the leader's writes
        // aren't paused.
        app.MapPost("/admin/snapshot", (HttpContext httpCtx, SnapshotRequest req) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;
            if (string.IsNullOrWhiteSpace(req.TargetPath))
                return Results.BadRequest(new { error = "targetPath is required." });
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                db.Snapshot(req.TargetPath);
                sw.Stop();
                long bytes = 0;
                try { bytes = new FileInfo(req.TargetPath).Length; } catch { }
                return Results.Ok(new
                {
                    success = true,
                    targetPath = req.TargetPath,
                    bytesCopied = bytes,
                    timeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1)
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Rebuild every index on a collection from scratch.
        // Needed after a bulk insert that used ?skipIndexes=true, or any time
        // an operator suspects index corruption.
        app.MapPost("/admin/rebuild-indexes/{collection}", (HttpContext httpCtx, string collection) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;
            try
            {
                var indexes = db.GetIndexes(collection);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                db.RebuildIndexes(collection);
                sw.Stop();
                return Results.Ok(new
                {
                    success = true,
                    collection,
                    indexesRebuilt = indexes.Count,
                    timeMs = sw.Elapsed.TotalMilliseconds
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Rebuild ONE named index. Surgical recovery for single-index drift
        // (e.g. issue #1 - a unique index out of sync with the collection).
        app.MapPost("/admin/rebuild-index/{collection}/{indexName}", (HttpContext httpCtx, string collection, string indexName) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ok = db.RebuildIndex(collection, indexName);
                sw.Stop();
                if (!ok)
                    return Results.NotFound(new { error = $"Collection '{collection}' or index '{indexName}' not found." });
                return Results.Ok(new
                {
                    success = true,
                    collection,
                    indexName,
                    timeMs = sw.Elapsed.TotalMilliseconds
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Compact (reclaim space from deletes) on a single collection
        app.MapPost("/admin/compact/{collection}", (HttpContext httpCtx, string collection) =>
        {
            if (ScopeCheck.RequireAdmin(httpCtx) is { } deny) return deny;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = db.Compact(collection);
                db.Flush();
                sw.Stop();
                return Results.Ok(new
                {
                    success = true,
                    collection,
                    pagesCompacted = r.PagesCompacted,
                    bytesReclaimed = r.BytesReclaimed,
                    timeMs = sw.Elapsed.TotalMilliseconds
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    // Issue #111 — fields that require a process restart to change; the config
    // API is explicit about these rather than silently ignoring an edit.
    private static readonly string[] RestartRequiredFields =
    {
        "port", "dataDir", "bindAllInterfaces", "insecureDevMode", "nodeName",
        "apiKey", "replicationSecret", "tls", "scopedKeys", "role",
        "leaderHost", "leaderPort", "replicationPort", "publicBaseUrl",
    };

    private static readonly string[] LiveEditableFields =
    {
        "minSyncReplicas", "syncTimeoutSeconds",
    };

    /// <summary>
    /// Issue #111 — the redacted, effective node-configuration view. Secrets are
    /// reduced to a boolean "configured" plus a short SHA-256 fingerprint so an
    /// operator can confirm <em>which</em> key is loaded without it ever leaving
    /// the box in plaintext. The semi-sync knobs are read from the LIVE engine.
    /// </summary>
    private static object BuildConfigView(DocumentForgeDb db, NodeConfig config)
    {
        var sec = config.Security;
        return new
        {
            node = new
            {
                nodeName = config.NodeName,
                port = config.Port,
                dataDir = config.DataDir,
                bindAllInterfaces = config.BindAllInterfaces,
                insecureDevMode = config.InsecureDevMode,
                httpEndpoint = config.ResolveHttpEndpoint(),
            },
            security = new
            {
                adminKeyConfigured = !string.IsNullOrEmpty(sec?.ApiKey),
                adminKeyFingerprint = Fingerprint(sec?.ApiKey),
                replicationSecretConfigured = !string.IsNullOrEmpty(sec?.ReplicationSecret),
                replicationSecretFingerprint = Fingerprint(sec?.ReplicationSecret),
                tls = new
                {
                    configured = sec?.Tls is not null,
                    certPath = sec?.Tls?.CertPath,
                    certPasswordConfigured = !string.IsNullOrEmpty(sec?.Tls?.CertPassword),
                },
                scopedKeys = (sec?.ScopedKeys ?? new List<ScopedApiKey>()).Select(k => new
                {
                    description = k.Description,
                    scopes = k.Scopes,
                    keyFingerprint = Fingerprint(k.Key),
                }).ToList(),
            },
            replication = new
            {
                role = config.Replication?.NormalizedRole is { Length: > 0 } r ? r : null,
                port = config.Replication?.Port,
                leaderHost = config.Replication?.LeaderHost,
                leaderPort = config.Replication?.LeaderPort,
                // Live engine values — reflect any PUT applied since startup.
                minSyncReplicas = db.MinSyncReplicas,
                syncTimeoutSeconds = db.SyncReplicationTimeout.TotalSeconds,
                autoFailover = config.Replication?.AutoFailover is { } af
                    ? new { silenceSeconds = af.SilenceSeconds, newLeaderPort = af.NewLeaderPort }
                    : null,
            },
            network = new { publicBaseUrl = config.Network?.PublicBaseUrl },
            restartRequired = RestartRequiredFields,
            liveEditable = LiveEditableFields,
        };
    }

    /// <summary>Non-reversible short fingerprint of a secret: "sha256:xxxxxxxx"
    /// (first 4 bytes of the digest, hex). Null/empty in → null out.</summary>
    private static string? Fingerprint(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        return "sha256:" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static void MapReplicationEndpoints(WebApplication app, DocumentForgeDb db, NodeConfig config)
    {
        // Current role + observability - safe for routine polling.
        //
        // The follower list and the follower's `leader.endpoint` are both
        // populated from socket-level state the engine already tracks
        // (FollowerCount, RemoteEndPoint, the configured _host:_port).
        // Admin UIs use these to draw the topology graph without operators
        // having to hand-tag each connection with a shard id (issue #12).
        //
        // Note: per-follower live ack tracking (`lastAckSeq`, `lagSeq`) is
        // intentionally not here. The wire protocol is fire-and-forget today
        // — we know each follower's seq AT HANDSHAKE only — and surfacing
        // a stale value as if it were live would be worse than omitting it.
        // Phase 2 of the multi-doc tx work (issue #13) is the natural place
        // to add ack framing; the lag fields land then.
        app.MapGet("/replication/status", () =>
        {
            var rep = config.Replication;
            var currentSeq = db.LeaderCurrentSeq;
            var followers = db.GetLogicalFollowers();
            return Results.Ok(new
            {
                node = config.NodeName,
                role = rep?.NormalizedRole ?? "none",
                readOnly = db.IsReadOnly,
                // Issue #51 — this node's own HTTP base URL. The admin-UI's
                // "Discover network" walk uses this to find peers without
                // having to guess the HTTP port from the replication
                // endpoint. Sourced from Network.PublicBaseUrl when set,
                // else derived from the bind address + port.
                httpEndpoint = config.ResolveHttpEndpoint(),
                leader = new
                {
                    currentSeq,
                    followerCount = db.GetLogicalFollowerCount(),
                    followers = followers.Select(f => new
                    {
                        endpoint = f.Endpoint,
                        // Issue #51 — null when the follower runs an older
                        // build that doesn't advertise its HTTP endpoint.
                        // Admin-UI falls back to its existing port-guess in
                        // that case.
                        httpEndpoint = f.HttpEndpoint,
                        connectedAt = f.ConnectedAtUtc,
                        handshakeSeq = f.HandshakeSeq,
                        // Worst-case lag: how far behind the follower was when it
                        // handshaked. If the link has stayed up, real lag is at
                        // most this; we can't claim less without acks.
                        worstCaseLagSeq = currentSeq > f.HandshakeSeq
                            ? currentSeq - f.HandshakeSeq : 0UL,
                    }).ToArray(),
                },
                follower = new
                {
                    lastAppliedSeq = db.FollowerLastSeq,
                    opsApplied = db.LogicallyReplicatedOps(),
                    gapsDetected = db.GapsDetected,
                    autoFailoverPromoted = db.WasAutoFailoverPromoted,
                    leader = db.LogicalFollowerLeaderEndpoint is null
                        ? null
                        // Issue #51 — leader.httpEndpoint is null until the
                        // bidirectional handshake exchange ships in a
                        // follow-up. For now the admin-UI falls back to
                        // probing the leader's HTTP port directly.
                        : (object)new { endpoint = db.LogicalFollowerLeaderEndpoint, httpEndpoint = (string?)null },
                }
            });
        });

        app.MapPost("/replication/start-leader", (StartLeaderRequest req) =>
        {
            try
            {
                db.StartLogicalReplicationServer(req.Port, req.SharedSecret ?? config.Security?.ReplicationSecret);
                return Results.Ok(new { success = true, role = "leader", port = req.Port });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/replication/start-follower", (StartFollowerRequest req) =>
        {
            try
            {
                db.StartLogicalReplicationFollower(req.Host, req.Port,
                    req.SharedSecret ?? config.Security?.ReplicationSecret,
                    ownHttpEndpoint: config.ResolveHttpEndpoint());
                return Results.Ok(new { success = true, role = "follower", leader = $"{req.Host}:{req.Port}" });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Manual promotion (planned handover step 2 or recovery after leader crash)
        app.MapPost("/replication/promote", (PromoteRequest req) =>
        {
            try
            {
                db.PromoteToLeader(req.Port);
                return Results.Ok(new { success = true, newRole = "leader", port = req.Port });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Read-only toggle (step 1 of planned handover - stops accepting writes)
        app.MapPost("/replication/read-only", () =>
        {
            db.EnterReadOnlyMode();
            return Results.Ok(new { success = true, readOnly = true });
        });

        app.MapPost("/replication/read-write", () =>
        {
            db.ExitReadOnlyMode();
            return Results.Ok(new { success = true, readOnly = false });
        });

        app.MapPost("/replication/auto-failover/enable", (AutoFailoverRequest req) =>
        {
            try
            {
                var silence = TimeSpan.FromSeconds(req.SilenceSeconds);
                db.EnableAutoFailover(req.NewLeaderPort, silence,
                    onPromoted: p => Console.WriteLine($"[dfdb] auto-failover: promoted to leader on :{p}"));
                return Results.Ok(new { success = true, silenceSeconds = req.SilenceSeconds, newLeaderPort = req.NewLeaderPort });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/replication/auto-failover/disable", () =>
        {
            db.DisableAutoFailover();
            return Results.Ok(new { success = true });
        });
    }

    private static readonly DateTime _startedAt = DateTime.UtcNow;

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>
    /// Whitelists the characters allowed in a JSON field path coming from a URL.
    /// Accepts names, dot notation, and bracket indices: passenger, passenger.lastName,
    /// flights[0].airport. Blocks everything that could form SQL syntax.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _fieldPathRegex =
        new("^[a-zA-Z_][a-zA-Z0-9_.\\[\\]]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidFieldPath(string field) =>
        !string.IsNullOrEmpty(field) && _fieldPathRegex.IsMatch(field);

    /// <summary>
    /// Issue #72 — resolve the database the caller wants this request
    /// to target. Priority (highest first):
    ///   <list type="number">
    ///     <item>Explicit <c>?database=name</c> query parameter</item>
    ///     <item><c>X-Database: name</c> request header</item>
    ///     <item>Returns <c>null</c> → caller falls back to the default DB</item>
    ///   </list>
    /// Path-scoped routes (<c>/db/{name}/...</c>) bypass this entirely —
    /// the name lives in the route value and is read directly.
    /// </summary>
    private static string? ResolveTargetDatabase(HttpContext ctx, string? queryDatabase)
    {
        if (!string.IsNullOrEmpty(queryDatabase)) return queryDatabase;
        var headerValue = ctx.Request.Headers["X-Database"].ToString();
        return string.IsNullOrEmpty(headerValue) ? null : headerValue;
    }

    /// <summary>
    /// Normalise an HTTP If-Match header value. RFC 9110 says ETags are quoted
    /// strong tokens (<c>"abc"</c>) or weak (<c>W/"abc"</c>); in practice
    /// hand-rolled clients often send the raw token. We accept both, strip the
    /// quotes / weak prefix, and let <see cref="EtagMismatchException"/> compare
    /// the unwrapped opaque string.
    /// </summary>
    private static string NormaliseIfMatch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        // Strip the weak-validator prefix.
        if (s.StartsWith("W/", StringComparison.Ordinal)) s = s[2..];
        // Strip surrounding quotes if present.
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s;
    }

    /// <summary>
    /// Whitelists collection names coming from a URL path segment before they
    /// are interpolated into SQL. Mirrors the query lexer's identifier rule
    /// ([a-zA-Z_][a-zA-Z0-9_]*) so every accepted name is also queryable; the
    /// length cap keeps a hostile name from bloating the single-page catalog.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _collectionNameRegex =
        new("^[a-zA-Z_][a-zA-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const int MaxCollectionNameLength = 128;

    internal static bool IsValidCollectionName(string name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxCollectionNameLength
        && _collectionNameRegex.IsMatch(name);

    private static IResult InvalidCollectionNameResult() =>
        Results.BadRequest(new { error = $"Collection name must match [a-zA-Z_][a-zA-Z0-9_]* and be at most {MaxCollectionNameLength} chars." });

    /// <summary>
    /// Issue #101 — the deny-by-default startup gate. Returns null when the
    /// node is allowed to start, otherwise a human-readable refusal reason.
    /// Rules, in order:
    ///   <list type="bullet">
    ///     <item>Any configured auth (admin key, scoped keys in node.json,
    ///           or persisted keys in _system) → allowed.</item>
    ///     <item>No auth + all-interfaces bind → refused, no override.</item>
    ///     <item>No auth + loopback → refused unless
    ///           <see cref="NodeConfig.InsecureDevMode"/> is set.</item>
    ///   </list>
    /// </summary>
    internal static string? ValidateStartupSecurity(NodeConfig config, int persistedKeyCount)
    {
        var authConfigured =
            !string.IsNullOrEmpty(config.Security?.ApiKey)
            || (config.Security?.ScopedKeys?.Count ?? 0) > 0
            || persistedKeyCount > 0;
        if (authConfigured) return null;

        if (config.BindAllInterfaces)
            return "refusing to bind all interfaces (--bind-all) with no authentication configured. " +
                   "This would expose an anonymous-admin database to the network. Set an API key first.";

        if (!config.InsecureDevMode)
            return "no authentication configured. Without a key every request has full admin access, " +
                   "so the server refuses to start by default.";

        return null; // loopback + explicit opt-in
    }

    /// <summary>
    /// True when a CORS Origin header points at this machine (localhost,
    /// 127.0.0.0/8, or [::1]) on any port/scheme. Used to pin dev-mode CORS
    /// to same-machine browsers only.
    /// </summary>
    internal static bool IsLoopbackOrigin(string origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback) return true;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}

// ---- DTOs ----
public record QueryRequest(string Sql);
public record SeedRequest(int? Orders);
public record CreateIndexRequest(string Collection, string Path, string? Name = null, bool Unique = false);
public record SnapshotRequest(string TargetPath);
public record StartLeaderRequest(int Port, string? SharedSecret = null);
public record StartFollowerRequest(string Host, int Port, string? SharedSecret = null);
public record PromoteRequest(int Port);
public record AutoFailoverRequest(int SilenceSeconds, int NewLeaderPort);
