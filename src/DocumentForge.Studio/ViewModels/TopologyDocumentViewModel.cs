using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.ViewModels;

/// <summary>A node in the replication topology graph.</summary>
public sealed class TopoNode
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 210;
    public double Height { get; init; } = 62;
    public Brush Fill { get; init; } = Brushes.White;
    public Brush Stroke { get; init; } = Brushes.Gray;

    public double CenterY => Y + Height / 2;
    public double Right => X + Width;
}

/// <summary>An edge (leader → follower) in the topology graph.</summary>
public sealed class TopoEdge
{
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public string Label { get; init; } = "";
    public double LabelX { get; init; }
    public double LabelY { get; init; }
}

/// <summary>A visual leader/follower map for a server connection, built by
/// reading each database's replication status. Client-only.</summary>
public sealed partial class TopologyDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;

    private static readonly Brush LeaderFill = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE));
    private static readonly Brush LeaderStroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x5D, 0xB0));
    private static readonly Brush FollowerFill = new SolidColorBrush(Color.FromRgb(0xE6, 0xF7, 0xEC));
    private static readonly Brush FollowerStroke = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57));
    private static readonly Brush NoneFill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush NoneStroke = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

    public TopologyDocumentViewModel(IDfConnection connection)
    {
        _connection = connection;
        Title = $"Topology — {connection.Descriptor.Name}";
        ContentId = $"topo:{connection.Descriptor.Id}";
    }

    static TopologyDocumentViewModel()
    {
        foreach (var b in new[] { LeaderFill, LeaderStroke, FollowerFill, FollowerStroke, NoneFill, NoneStroke })
            b.Freeze();
    }

    public ObservableCollection<TopoNode> Nodes { get; } = new();
    public ObservableCollection<TopoEdge> Edges { get; } = new();

    [ObservableProperty] private double _canvasWidth = 600;
    [ObservableProperty] private double _canvasHeight = 400;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Nodes.Clear();
        Edges.Clear();
        try
        {
            const double leftX = 30, rightX = 380, nodeH = 62, gapY = 22, nodeW = 210;
            double y = 24;
            var leaders = 0;
            var followers = 0;

            var databases = await _connection.GetDatabasesAsync();
            foreach (var db in databases)
            {
                ReplicationStatusOrNull status = await SafeStatus(db.Name);

                var role = status.Role;
                if (role == "leader") leaders++;
                else if (role == "follower") followers++;

                var (fill, stroke) = role switch
                {
                    "leader" => (LeaderFill, LeaderStroke),
                    "follower" => (FollowerFill, FollowerStroke),
                    _ => (NoneFill, NoneStroke),
                };

                var subtitle = role switch
                {
                    "leader" => $"leader · port {status.LeaderPort} · seq {status.CurrentSeq:N0} · {status.FollowerCount} follower(s)",
                    "follower" => $"follower · applied {status.FollowerLastAppliedSeq:N0}{(status.GapsDetected ? " · ⚠ gaps" : "")}",
                    "none" => "standalone (no replication)",
                    _ => status.Error ?? "unknown",
                };

                var dbNode = new TopoNode
                {
                    Title = db.Name,
                    Subtitle = subtitle,
                    X = leftX,
                    Y = y,
                    Width = nodeW,
                    Fill = fill,
                    Stroke = stroke,
                };
                Nodes.Add(dbNode);

                var peerY = y;
                if (role == "leader")
                {
                    foreach (var f in status.Followers)
                    {
                        // The leader only knows a follower by its replication-link
                        // socket (an ephemeral TCP port, not connectable) unless the
                        // follower advertised an HTTP endpoint. Label honestly so
                        // nobody tries to connect to the ephemeral port.
                        var connectable = !string.IsNullOrWhiteSpace(f.HttpEndpoint);
                        var peer = new TopoNode
                        {
                            Title = connectable ? f.HttpEndpoint! : f.Endpoint,
                            Subtitle = connectable
                                ? $"follower · lag {f.LagSeq:N0}"
                                : $"follower · replication link (not HTTP) · lag {f.LagSeq:N0}",
                            X = rightX, Y = peerY, Width = nodeW,
                            Fill = FollowerFill, Stroke = FollowerStroke,
                        };
                        Nodes.Add(peer);
                        AddEdge(dbNode, peer, $"lag {f.LagSeq:N0}");
                        peerY += nodeH + gapY;
                    }
                }
                else if (role == "follower" && status.FollowerLeaderEndpoint is { } leaderEp)
                {
                    var peer = new TopoNode
                    {
                        Title = leaderEp,
                        Subtitle = "leader · replication link (not HTTP)",
                        X = rightX, Y = peerY, Width = nodeW,
                        Fill = LeaderFill, Stroke = LeaderStroke,
                    };
                    Nodes.Add(peer);
                    AddEdge(peer, dbNode, "");
                    peerY += nodeH + gapY;
                }

                y = Math.Max(y + nodeH + gapY, peerY) + gapY;
            }

            CanvasWidth = rightX + nodeW + 40;
            CanvasHeight = Math.Max(y + 20, 200);
            StatusMessage = databases.Count == 0
                ? "No databases."
                : $"{databases.Count} database(s) · {leaders} leader(s) · {followers} follower(s). Refreshed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddEdge(TopoNode from, TopoNode to, string label) =>
        Edges.Add(new TopoEdge
        {
            X1 = from.Right,
            Y1 = from.CenterY,
            X2 = to.X,
            Y2 = to.CenterY,
            Label = label,
            LabelX = (from.Right + to.X) / 2 - 20,
            LabelY = (from.CenterY + to.CenterY) / 2 - 16,
        });

    private async Task<ReplicationStatusOrNull> SafeStatus(string database)
    {
        try
        {
            var s = await _connection.GetReplicationStatusAsync(database);
            return new ReplicationStatusOrNull(s.Role, s.LeaderPort, s.CurrentSeq, s.FollowerCount,
                s.Followers, s.FollowerLastAppliedSeq, s.GapsDetected, s.FollowerLeaderEndpoint, null);
        }
        catch (Exception ex)
        {
            return new ReplicationStatusOrNull("unknown", 0, 0, 0,
                Array.Empty<Core.Models.ReplicationFollowerInfo>(), 0, false, null, ex.Message);
        }
    }

    private sealed record ReplicationStatusOrNull(
        string Role, int LeaderPort, long CurrentSeq, int FollowerCount,
        IReadOnlyList<Core.Models.ReplicationFollowerInfo> Followers,
        long FollowerLastAppliedSeq, bool GapsDetected, string? FollowerLeaderEndpoint, string? Error);
}
