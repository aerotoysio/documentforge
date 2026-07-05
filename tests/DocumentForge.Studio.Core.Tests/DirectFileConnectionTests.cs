using DocumentForge.Core;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.Core.Tests;

public sealed class DirectFileConnectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dfstudio-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public DirectFileConnectionTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "unit.dfdb");
        DirectFileConnection.CreateDatabaseFile(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ConnectionDescriptor Descriptor(string path) => new()
    {
        Name = "unit",
        Kind = ConnectionKind.File,
        FilePath = path,
    };

    [Fact]
    public async Task Connect_Browse_Query_EndToEnd()
    {
        await using var connection = new DirectFileConnection(Descriptor(_dbPath));
        await connection.ConnectAsync();

        var databases = await connection.GetDatabasesAsync();
        var db = Assert.Single(databases);
        Assert.Equal("unit", db.Name);

        var insert = await connection.ExecuteAsync(db.Name,
            """INSERT INTO users VALUES { "name": "Alice", "role": "admin" }""");
        Assert.True(insert.Success);
        Assert.Equal(1, insert.AffectedCount);

        var collections = await connection.GetCollectionNamesAsync(db.Name);
        Assert.Contains("users", collections);

        await connection.ExecuteAsync(db.Name, "CREATE INDEX idx_name ON users(name)");
        var indexes = await connection.GetIndexesAsync(db.Name, "users");
        Assert.Contains(indexes, i => i.Name == "idx_name" && i.JsonPath == "name");

        var select = await connection.ExecuteAsync(db.Name, "SELECT * FROM users WHERE name = 'Alice'");
        Assert.True(select.Success);
        var doc = Assert.Single(select.Documents);
        Assert.Contains("Alice", doc);
        Assert.NotNull(select.Plan);

        var stats = await connection.GetStatsAsync(db.Name);
        Assert.Contains(stats.Collections, c => c.Name == "users" && c.DocumentCount == 1);

        var health = await connection.GetHealthAsync();
        Assert.True(health.Healthy);
    }

    [Fact]
    public async Task Dispose_Releases_The_File_Lock()
    {
        var connection = new DirectFileConnection(Descriptor(_dbPath));
        await connection.ConnectAsync();
        await connection.DisposeAsync();

        // If the lock were still held this second open would throw.
        await using var second = new DirectFileConnection(Descriptor(_dbPath));
        await second.ConnectAsync();
        Assert.True(second.IsConnected);
    }

    [Fact]
    public async Task Locked_File_Surfaces_Holder_Details()
    {
        await using var first = new DirectFileConnection(Descriptor(_dbPath));
        await first.ConnectAsync();

        await using var second = new DirectFileConnection(Descriptor(_dbPath));
        var ex = await Assert.ThrowsAsync<DatabaseLockedException>(() => second.ConnectAsync());
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_File_Fails_With_Clear_Message()
    {
        await using var connection = new DirectFileConnection(Descriptor(Path.Combine(_dir, "nope.dfdb")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => connection.ConnectAsync());
    }

    [Fact]
    public async Task Create_And_Drop_Database_Are_Not_Supported()
    {
        await using var connection = new DirectFileConnection(Descriptor(_dbPath));
        await connection.ConnectAsync();
        Assert.Equal(ConnectionCapabilities.None, connection.Capabilities);
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.CreateDatabaseAsync("x"));
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.DropDatabaseAsync("x", deleteFiles: false));
    }
}
