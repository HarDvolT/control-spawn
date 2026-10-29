namespace Prism.Router.Tests;

public class ScannerTests
{
    private static readonly Dictionary<string, object?> Data = D.Map(
        ("network_operator", "Ooredoo TN"),
        ("PCI", "272+272"),
        ("currentband", "1+3"),
        ("CELL_ID", "1E24A03"),
        ("RSRP", "-92"),
        ("RSRQ", "-14.0"),
        ("SINR", "0.0"),
        ("RSRP_5G", "-97"),
        ("RSRQ_5G", "-11"),
        ("SINR_5G", "8"),
        ("PCI_5G", "715"),
        ("currentband_5g", "78"),
        ("lte_info", "1850,272,-92,-14,0;1850,301,-89,-12,4;100,118,-101,-17,-3;"),
        ("nr_info", ""),
        ("plmn", "60503"));

    [Fact]
    public void DecodeEci()
    {
        var plain = Scanner.DecodeEci("1E24A03");
        Assert.NotNull(plain);
        Assert.Equal(0x1E24A, plain!.Value.Enb);
        Assert.Equal(0x03, plain.Value.Cell);

        var prefixed = Scanner.DecodeEci("0x1E24A03");
        Assert.NotNull(prefixed);
        Assert.Equal(0x1E24A, prefixed!.Value.Enb);
        Assert.Equal(0x03, prefixed.Value.Cell);

        Assert.Null(Scanner.DecodeEci(""));
        Assert.Null(Scanner.DecodeEci("zz"));
    }

    [Fact]
    public void ParseNeighbors()
    {
        var neighbors = Scanner.ParseNeighbors("1850,301,-89,-12,4;;bad;100,118,,,;", "4G");
        Assert.Equal(new[] { "301", "118" }, neighbors.Select(n => n.Pci));
        Assert.Equal(4.0, neighbors[0].Sinr);
        Assert.Null(neighbors[1].Rsrp);
        Assert.Empty(Scanner.ParseNeighbors("", "5G"));
    }

    [Fact]
    public void AnalyzeMarksServingAndStronger()
    {
        var scan = Scanner.Analyze(Data);
        var byPci = scan.Neighbors.ToDictionary(n => n.Pci, n => n);
        Assert.True(byPci["272"].IsServing);
        Assert.False(byPci["272"].Stronger);
        Assert.True(byPci["301"].Stronger);      // SINR 4 > 0 and RSRP -89 > -92
        Assert.False(byPci["118"].Stronger);
        Assert.Equal(0x1E24A, scan.Lte.Enb!.Value);
        Assert.Equal("B1+B3", scan.Lte.Band);
        Assert.Equal("n78", scan.Nr!.Band);
        Assert.Equal("60503", scan.Plmn);
    }

    [Fact]
    public void No5G()
    {
        var data = Data.With(("RSRP_5G", ""), ("SINR_5G", ""), ("RSRQ_5G", ""));
        Assert.Null(Scanner.Analyze(data).Nr);
    }

    [Fact]
    public void BestTrackerAndCopy()
    {
        var tracker = new Scanner.BestTracker();
        tracker.Update(Scanner.Analyze(Data), now: 100);

        var better = Data.With(("SINR_5G", "15"), ("lte_info", "1850,301,-85,-10,9;"));
        tracker.Update(Scanner.Analyze(better), now: 200);
        tracker.Update(Scanner.Analyze(Data), now: 300);

        Assert.Equal(15, tracker.BestSinr!.Sinr);
        Assert.Equal(200, tracker.BestSinr.At);
        Assert.Equal("301", tracker.BestNeighbor!.Pci);
        Assert.Equal(9, tracker.BestNeighbor.Sinr);
        Assert.Equal(
            "ZLT X17M best cell seen: PCI 301, EARFCN 1850, band B1+B3, SINR 9, RSRP -85, PLMN 60503",
            tracker.CopyText());
    }

    [Fact]
    public void RealRouterFields()
    {
        // Shape of the ZLT X17M reply: uppercase PLMN, FREQ per carrier, no neighbor lists.
        var data = D.Copy(Data);
        data.Remove("lte_info");
        data.Remove("nr_info");
        data.Remove("plmn");
        data["PLMN"] = "60503";
        data["CELL_ID"] = "05DF317";
        data["FREQ"] = "101+1800";
        data["FREQ_5G"] = "628704";

        var scan = Scanner.Analyze(data);
        Assert.Equal("60503", scan.Plmn);
        Assert.Equal(24051, scan.Lte.Enb!.Value);
        Assert.Empty(scan.Neighbors);

        var tracker = new Scanner.BestTracker();
        tracker.Update(scan);
        Assert.Equal(
            "ZLT X17M best cell seen: PCI 715, EARFCN 628704, band n78, SINR 8, RSRP -97, PLMN 60503",
            tracker.CopyText(scan));
    }

    [Fact]
    public void Tone()
    {
        Assert.Equal(300, Scanner.ToneHz(0));
        Assert.Equal(900, Scanner.ToneHz(20));
        Assert.Equal(900, Scanner.ToneHz(50));
        Assert.Equal(300, Scanner.ToneHz(-5));
        Assert.Null(Scanner.ToneHz(null));
    }
}
