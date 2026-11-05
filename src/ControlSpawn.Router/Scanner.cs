using System.Globalization;

namespace ControlSpawn.Router;

/// <summary>A serving cell (4G anchor or 5G).</summary>
public sealed class Cell
{
    public string Type { get; init; } = "";

    public string Band { get; init; } = "";

    public string Pci { get; init; } = "";

    public IReadOnlySet<string> Pcis { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>e.g. "101+1800", one per carrier; for 5G this is the NR-ARFCN.</summary>
    public string Earfcn { get; init; } = "";

    public long? Enb { get; init; }

    /// <summary>Python's "cell": the 8-bit cell part of the ECI.</summary>
    public int? CellId { get; init; }

    public double? Rsrp { get; init; }

    public double? Rsrq { get; init; }

    public double? Sinr { get; init; }
}

/// <summary>A neighbour cell from the router's lte_info / nr_info lists.</summary>
public sealed class Neighbor
{
    public string Type { get; init; } = "";

    public string Earfcn { get; init; } = "";

    public string Pci { get; init; } = "";

    public double? Rsrp { get; init; }

    public double? Rsrq { get; init; }

    public double? Sinr { get; init; }

    public bool IsServing { get; set; }

    public bool Stronger { get; set; }

    /// <summary>Timestamp added by <see cref="BestTracker"/>; not part of a fresh scan.</summary>
    public double? At { get; set; }

    public Neighbor Clone() => new()
    {
        Type = Type,
        Earfcn = Earfcn,
        Pci = Pci,
        Rsrp = Rsrp,
        Rsrq = Rsrq,
        Sinr = Sinr,
        IsServing = IsServing,
        Stronger = Stronger,
        At = At,
    };
}

/// <summary>Serving cells + neighbours + PLMN from one signal reply.</summary>
public sealed class ScanResult
{
    public Cell Lte { get; init; } = new() { Type = "4G" };

    public Cell? Nr { get; init; }

    public IReadOnlyList<Neighbor> Neighbors { get; init; } = Array.Empty<Neighbor>();

    public string Plmn { get; init; } = "";
}

/// <summary>A remembered best reading: SINR or RSRP of the active cell, with when it was seen.</summary>
public sealed class BestReading
{
    public double? Sinr { get; init; }

    public double? Rsrp { get; init; }

    public string Type { get; init; } = "";

    public double At { get; init; }
}

/// <summary>
/// Scanner / placement helpers: serving cell details, neighbour parsing, best-spot tracking.
/// Pure functions only (no UI, no network) so they are easy to test.
/// </summary>
public static class Scanner
{
    /// <summary>CELL_ID is the 28-bit E-UTRAN cell id in hex. Returns (eNodeB, cell) or null.</summary>
    public static (long Enb, int Cell)? DecodeEci(string? cellId)
    {
        var text = (cellId ?? "").Trim().ToLowerInvariant();
        if (text.StartsWith("0x", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            var eci = Convert.ToInt64(text, 16);
            return (eci >> 8, (int)(eci & 0xFF));
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static HashSet<string> Pcis(object? value)
    {
        return Raw.Str(value)
            .Split('+')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string FindPlmn(IReadOnlyDictionary<string, object?> data)
    {
        foreach (var key in new[] { "plmn", "PLMN", "network_plmn", "plmn_id", "PLMN_ID" })
        {
            var value = Raw.Str(data.GetValueOrDefault(key)).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        var mcc = Raw.Str(data.GetValueOrDefault("mcc") ?? data.GetValueOrDefault("MCC")).Trim();
        var mnc = Raw.Str(data.GetValueOrDefault("mnc") ?? data.GetValueOrDefault("MNC")).Trim();
        return mcc.Length > 0 && mnc.Length > 0 ? mcc + mnc : "";
    }

    /// <summary>Serving 4G anchor and 5G cell (5G is null when not connected).</summary>
    public static (Cell Lte, Cell? Nr) ServingCells(IReadOnlyDictionary<string, object?> data)
    {
        var lteBand = string.Join(
            "+",
            Raw.Str(data.GetValueOrDefault("currentband"))
                .Split('+')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Select(part => "B" + part));

        var eci = DecodeEci(Raw.Str(data.GetValueOrDefault("CELL_ID")));
        var lte = new Cell
        {
            Type = "4G",
            Band = lteBand,
            Pci = Raw.Str(data.GetValueOrDefault("PCI")).Trim(),
            Pcis = Pcis(data.GetValueOrDefault("PCI")),
            Earfcn = Raw.Str(data.GetValueOrDefault("FREQ")).Trim(),
            Enb = eci?.Enb,
            CellId = eci?.Cell,
            Rsrp = Raw.ToFloat(data.GetValueOrDefault("RSRP")),
            Rsrq = Raw.ToFloat(data.GetValueOrDefault("RSRQ")),
            Sinr = Raw.ToFloat(data.GetValueOrDefault("SINR")),
        };

        var band5G = Raw.Str(data.GetValueOrDefault("currentband_5g")).Trim();
        Cell? nr = new Cell
        {
            Type = "5G",
            Band = band5G.Length > 0 ? "n" + band5G : "",
            Pci = Raw.Str(data.GetValueOrDefault("PCI_5G")).Trim(),
            Pcis = Pcis(data.GetValueOrDefault("PCI_5G")),
            Earfcn = Raw.Str(data.GetValueOrDefault("FREQ_5G")).Trim(),
            Rsrp = Raw.ToFloat(data.GetValueOrDefault("RSRP_5G")),
            Rsrq = Raw.ToFloat(data.GetValueOrDefault("RSRQ_5G")),
            Sinr = Raw.ToFloat(data.GetValueOrDefault("SINR_5G")),
        };

        if (nr.Rsrp is null && nr.Sinr is null)
        {
            nr = null;
        }

        return (lte, nr);
    }

    /// <summary>"EARFCN,PCI,RSRP,RSRQ,SINR;..." -> list of neighbours. Bad entries are skipped.</summary>
    public static List<Neighbor> ParseNeighbors(object? text, string kind)
    {
        var result = new List<Neighbor>();
        foreach (var chunk in Raw.Str(text).Split(';'))
        {
            var parts = chunk.Split(',').Select(part => part.Trim()).ToList();
            if (parts.Count < 2 || !parts.Any(part => part.Length > 0))
            {
                continue;
            }

            while (parts.Count < 5)
            {
                parts.Add("");
            }

            var earfcn = parts[0];
            var pci = parts[1];
            var rsrp = parts[2];
            var rsrq = parts[3];
            var sinr = parts[4];
            if (pci.Length == 0)
            {
                continue;
            }

            result.Add(new Neighbor
            {
                Type = kind,
                Earfcn = earfcn,
                Pci = pci,
                Rsrp = Raw.ToFloat(rsrp),
                Rsrq = Raw.ToFloat(rsrq),
                Sinr = Raw.ToFloat(sinr),
            });
        }

        return result;
    }

    private static bool Gt(double? a, double? b) => a is not null && b is not null && a > b;

    /// <summary>Serving cells + neighbours, each neighbour marked serving / stronger.</summary>
    public static ScanResult Analyze(IReadOnlyDictionary<string, object?> data)
    {
        var (lte, nr) = ServingCells(data);
        var neighbors = ParseNeighbors(data.GetValueOrDefault("lte_info"), "4G");
        neighbors.AddRange(ParseNeighbors(data.GetValueOrDefault("nr_info"), "5G"));

        foreach (var neighbor in neighbors)
        {
            var serving = neighbor.Type == "4G" ? lte : nr;
            neighbor.IsServing = serving is not null && serving.Pcis.Contains(neighbor.Pci);
            neighbor.Stronger = serving is not null
                && !neighbor.IsServing
                && (Gt(neighbor.Sinr, serving.Sinr) || Gt(neighbor.Rsrp, serving.Rsrp));
        }

        return new ScanResult
        {
            Lte = lte,
            Nr = nr,
            Neighbors = neighbors,
            Plmn = FindPlmn(data),
        };
    }

    /// <summary>Rank cells: SINR first, RSRP as tie-breaker. Missing values rank lowest.</summary>
    private static (double Sinr, double Rsrp) Score(Neighbor cell) => (
        cell.Sinr ?? -999,
        cell.Rsrp ?? -999);

    public static int CompareScore(Neighbor left, Neighbor right) => Score(left).CompareTo(Score(right));

    private static string Fmt(double? value) =>
        value is null ? "?" : value.Value.ToString("G", CultureInfo.InvariantCulture);

    /// <summary>Chooses the best cell seen and writes the one-line summary for the operator.</summary>
    public sealed class BestTracker
    {
        /// <summary>The best neighbour seen, with its timestamp.</summary>
        public Neighbor? BestNeighbor { get; private set; }

        public BestReading? BestSinr { get; private set; }

        public BestReading? BestRsrp { get; private set; }

        public string Plmn { get; private set; } = "";

        public Dictionary<string, string> Bands { get; } = new(StringComparer.Ordinal)
        {
            ["4G"] = "",
            ["5G"] = "",
        };

        public void Update(ScanResult scan, double? now = null)
        {
            var at = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            if (scan.Plmn.Length > 0)
            {
                Plmn = scan.Plmn;
            }

            foreach (var cell in new[] { scan.Lte, scan.Nr })
            {
                if (cell is not null && cell.Band.Length > 0)
                {
                    Bands[cell.Type] = cell.Band;
                }
            }

            var active = scan.Nr ?? scan.Lte;
            if (active.Sinr is not null && (BestSinr is null || active.Sinr > BestSinr.Sinr))
            {
                BestSinr = new BestReading
                {
                    Sinr = active.Sinr,
                    Rsrp = active.Rsrp,
                    Type = active.Type,
                    At = at,
                };
            }

            if (active.Rsrp is not null && (BestRsrp is null || active.Rsrp > BestRsrp.Rsrp))
            {
                BestRsrp = new BestReading
                {
                    Rsrp = active.Rsrp,
                    Sinr = active.Sinr,
                    Type = active.Type,
                    At = at,
                };
            }

            foreach (var neighbor in scan.Neighbors)
            {
                if (neighbor.Sinr is null && neighbor.Rsrp is null)
                {
                    continue;
                }

                if (BestNeighbor is null || CompareScore(neighbor, BestNeighbor) > 0)
                {
                    var copy = neighbor.Clone();
                    copy.At = at;
                    BestNeighbor = copy;
                }
            }
        }

        /// <summary>One-line summary to send to Ooredoo when asking for a cell lock.</summary>
        public string CopyText(ScanResult? scan = null)
        {
            var best = BestNeighbor;
            string pci;
            string earfcn;
            string band;
            double? sinr;
            double? rsrp;
            if (best is null && scan is not null)
            {
                var cell = scan.Nr ?? scan.Lte;
                pci = cell.Pci.Length > 0 ? cell.Pci : "?";
                earfcn = cell.Earfcn.Length > 0 ? cell.Earfcn : "?";
                band = cell.Band.Length > 0 ? cell.Band : "?";
                sinr = cell.Sinr;
                rsrp = cell.Rsrp;
            }
            else if (best is not null)
            {
                pci = best.Pci;
                earfcn = best.Earfcn.Length > 0 ? best.Earfcn : "?";
                band = Bands.TryGetValue(best.Type, out var remembered) && remembered.Length > 0 ? remembered : "?";
                sinr = best.Sinr;
                rsrp = best.Rsrp;
            }
            else
            {
                return "";
            }

            return $"ZLT X17M best cell seen: PCI {pci}, EARFCN {earfcn}, band {band}, "
                + $"SINR {Fmt(sinr)}, RSRP {Fmt(rsrp)}, PLMN {(Plmn.Length > 0 ? Plmn : "?")}";
        }
    }

    /// <summary>SINR 0 -> 300 Hz, 20 -> 900 Hz (clamped).</summary>
    public static int? ToneHz(double? sinr)
    {
        if (sinr is null)
        {
            return null;
        }

        return (int)(300 + Math.Max(0.0, Math.Min(20.0, sinr.Value)) / 20.0 * 600);
    }
}
