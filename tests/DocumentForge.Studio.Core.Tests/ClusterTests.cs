using DocumentForge.Studio.Core.Cluster;

namespace DocumentForge.Studio.Core.Tests;

public sealed class ClusterConfigTests
{
    [Fact]
    public void RoundTrips_The_Engine_Json_Shape()
    {
        // Shape as written by the engine's ClusterConfig (PascalCase, string enum).
        var json = """
        {
          "Version": 1,
          "Shards": [
            { "Name": "shard-a", "Endpoint": "http://a:5001", "Leader": "http://a:5001", "Followers": ["http://a2:5002"] }
          ],
          "Collections": {
            "orders": { "Strategy": "Hash", "ShardKeyPath": "customerId" },
            "airports": { "Strategy": "Replicated", "ShardKeyPath": null }
          },
          "VirtualNodesPerShard": 150,
          "Migration": null
        }
        """;

        var config = ClusterConfig.FromJson(json);

        var shard = Assert.Single(config.Shards);
        Assert.Equal("shard-a", shard.Name);
        Assert.Equal("http://a:5001", shard.LeaderEndpoint);
        Assert.Single(shard.Followers);

        Assert.Equal(ShardingStrategy.Hash, config.Collections["orders"].Strategy);
        Assert.Equal("customerId", config.Collections["orders"].ShardKeyPath);
        Assert.Equal(ShardingStrategy.Replicated, config.Collections["airports"].Strategy);

        // Re-emit and confirm it parses back identically (engine-compatible).
        var reparsed = ClusterConfig.FromJson(config.ToJson());
        Assert.Equal(config.Shards.Count, reparsed.Shards.Count);
        Assert.Equal(ShardingStrategy.Hash, reparsed.Collections["orders"].Strategy);
        Assert.Contains("\"Hash\"", config.ToJson());        // string enum, not a number
        Assert.Contains("\"ShardKeyPath\"", config.ToJson()); // PascalCase property names
    }

    [Fact]
    public void New_Config_Has_Sensible_Defaults()
    {
        var config = new ClusterConfig();
        Assert.Equal(1, config.Version);
        Assert.Equal(150, config.VirtualNodesPerShard);
        Assert.Empty(config.Shards);
        Assert.Empty(config.Collections);
    }

    [Fact]
    public void Save_And_Load_File_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cluster-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ClusterConfig();
            config.Shards.Add(new ShardDescriptor { Name = "s1", Endpoint = "http://s1:5001" });
            config.Collections["orders"] = new CollectionPolicyDescriptor { Strategy = ShardingStrategy.Hash, ShardKeyPath = "id" };
            config.Save(path);

            var loaded = ClusterConfig.Load(path);
            Assert.Equal("s1", loaded.Shards[0].Name);
            Assert.Equal(ShardingStrategy.Hash, loaded.Collections["orders"].Strategy);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
