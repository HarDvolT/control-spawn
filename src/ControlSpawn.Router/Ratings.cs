using System.Globalization;

namespace ControlSpawn.Router;

/// <summary>The three signal metrics the widget rates.</summary>
public enum Metric
{
    Rsrp,
    Rsrq,
    Sinr,
}

/// <summary>Signal ratings, parsing of the router's signal reply, scores, bars and trend.</summary>
public static class Ratings
{
    public const string Excellent = "Excellent";
    public const string Good = "Good";
    public const string Fair = "Fair";
    public const string Poor = "Poor";

    /// <summary>Best to worst; used to pick the worst rating and to rank metrics.</summary>
    public static readonly IReadOnlyList<string> Order = new[] { Excellent, Good, Fair, Poor };

    public static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        [Excellent] = "#3ddc84",
        [Good] = "#a3d65c",
        [Fair] = "#f5b942",
        [Poor] = "#ef5350",
    };

    /// <summary>Colour for "no data": the Python COLORS[None].</summary>
    public const string NoDataColor = "#7a7f87";

    /// <summary>(excellentMin, goodMin, fairMin); anything below fairMin is Poor.</summary>
    public static readonly IReadOnlyDictionary<Metric, (double ExcellentMin, double GoodMin, double FairMin)> Thresholds =
        new Dictionary<Metric, (double, double, double)>
        {
            [Metric.Rsrp] = (-80, -90, -100),
            [Metric.Sinr] = (20, 13, 0),
            [Metric.Rsrq] = (-10, -15, -20),
        };

    /// <summary>0-100 score per reading; the rating thresholds land on 40 / 60 / 80.</summary>
    public static readonly IReadOnlyDictionary<Metric, IReadOnlyList<(double X, double Y)>> ScorePoints =
        new Dictionary<Metric, IReadOnlyList<(double X, double Y)>>
        {
            [Metric.Rsrp] = new[] { (-120d, 0d), (-100d, 40d), (-90d, 60d), (-80d, 80d), (-70d, 100d) },
            [Metric.Sinr] = new[] { (-5d, 0d), (0d, 40d), (13d, 60d), (20d, 80d), (30d, 100d) },
        };

    public static readonly IReadOnlyList<(double Min, string Rating)> ScoreBands = new[]
    {
        (80d, Excellent),
        (60d, Good),
        (40d, Fair),
        (0d, Poor),
    };

    public const double TrendSeconds = 30;

    public static string MetricName(Metric metric) => metric switch
    {
        Metric.Rsrp => "RSRP",
        Metric.Rsrq => "RSRQ",
        _ => "SINR",
    };

    /// <summary>Colour for a rating, or <see cref="NoDataColor"/> when there is none.</summary>
    public static string ColorFor(string? rating)
    {
        if (rating is not null && Colors.TryGetValue(rating, out var color))
        {
            return color;
        }

        return NoDataColor;
    }

    public static int RatingIndex(string rating) => Order.ToList().IndexOf(rating);

    public static string? Rate(Metric metric, double? value)
    {
        if (value is null)
        {
            return null;
        }

        var (excellentMin, goodMin, fairMin) = Thresholds[metric];
        if (value >= excellentMin)
        {
            return Excellent;
        }

        if (value >= goodMin)
        {
            return Good;
        }

        return value >= fairMin ? Fair : Poor;
    }

    /// <summary>The worst of the given ratings, ignoring missing ones; null when none are present.</summary>
    public static string? Worst(params string?[] ratings)
    {
        string? worst = null;
        var worstIndex = -1;
        foreach (var rating in ratings)
        {
            if (rating is null)
            {
                continue;
            }

            var index = RatingIndex(rating);
            if (index > worstIndex)
            {
                worst = rating;
                worstIndex = index;
            }
        }

        return worst;
    }

    public static double? MetricScore(Metric metric, double? value)
    {
        if (value is null)
        {
            return null;
        }

        var points = ScorePoints[metric];
        if (value <= points[0].X)
        {
            return 0.0;
        }

        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (x0, y0) = points[i];
            var (x1, y1) = points[i + 1];
            if (value <= x1)
            {
                return y0 + (value.Value - x0) / (x1 - x0) * (y1 - y0);
            }
        }

        return 100.0;
    }

    /// <summary>The weaker of signal strength and signal vs. noise, like the overall rating.</summary>
    public static double? LinkScore(Link link)
    {
        var scores = new List<double>();
        foreach (var metric in new[] { Metric.Rsrp, Metric.Sinr })
        {
            var score = MetricScore(metric, link.Values[metric]);
            if (score is not null)
            {
                scores.Add(score.Value);
            }
        }

        return scores.Count > 0 ? Math.Round(scores.Min()) : null;
    }

    public static string? ScoreRating(double? score)
    {
        if (score is null)
        {
            return null;
        }

        foreach (var (min, rating) in ScoreBands)
        {
            if (score >= min)
            {
                return rating;
            }
        }

        return Poor;
    }

    /// <summary>
    /// Samples are (time, score) pairs. Returns "up", "down", "steady", or null if too few
    /// readings. Uses the least-squares slope over the last <see cref="TrendSeconds"/> seconds.
    /// </summary>
    public static string? Trend(IEnumerable<(double Time, double? Score)> samples, double now)
    {
        var points = samples
            .Where(sample => sample.Score is not null && now - sample.Time <= TrendSeconds)
            .Select(sample => (Time: sample.Time, Score: sample.Score!.Value))
            .ToList();

        if (points.Count < 3 || points[^1].Time - points[0].Time < 5)
        {
            return null;
        }

        var meanTime = points.Average(p => p.Time);
        var meanValue = points.Average(p => p.Score);
        var denominator = points.Sum(p => (p.Time - meanTime) * (p.Time - meanTime));
        if (denominator == 0)
        {
            return null;
        }

        var covariance = points.Sum(p => (p.Time - meanTime) * (p.Score - meanValue));
        var change = covariance / denominator * (points[^1].Time - points[0].Time);

        if (change >= 4)
        {
            return "up";
        }

        if (change <= -4)
        {
            return "down";
        }

        return "steady";
    }

    /// <summary>One link's values, ratings, and whether the router reported anything for it.</summary>
    public static Link ReadLink(IReadOnlyDictionary<string, object?> data, string suffix)
    {
        var values = new Dictionary<Metric, double?>
        {
            [Metric.Rsrp] = Raw.ToFloat(data.GetValueOrDefault("RSRP" + suffix)),
            [Metric.Rsrq] = Raw.ToFloat(data.GetValueOrDefault("RSRQ" + suffix)),
            [Metric.Sinr] = Raw.ToFloat(data.GetValueOrDefault("SINR" + suffix)),
        };
        return new Link(values);
    }

    /// <summary>"5G(NSA)" -> "5G NSA", "4G" -> "4G".</summary>
    public static string NetworkLabel(string raw)
    {
        return raw.Length > 0
            ? raw.Replace("(", " ").Replace(")", "").Trim()
            : "No service";
    }

    public static SignalSummary Summarize(IReadOnlyDictionary<string, object?> data)
    {
        var lte = ReadLink(data, "");
        var nr = ReadLink(data, "_5G");
        var main = nr.Present ? nr : lte;

        var overall = Worst(main.Ratings[Metric.Rsrp], main.Ratings[Metric.Sinr]);

        var band = "";
        var band5G = Raw.Str(data.GetValueOrDefault("currentband_5g")).Trim();
        var band4G = Raw.Str(data.GetValueOrDefault("currentband")).Trim();
        if (nr.Present && band5G.Length > 0)
        {
            band = "n" + band5G;
        }
        else if (band4G.Length > 0)
        {
            band = string.Join("+", band4G.Split('+').Where(b => b.Length > 0).Select(b => "B" + b));
        }

        var bars = (int)Math.Clamp(Raw.ToInt(data.GetValueOrDefault("signal_lvl")) ?? 0, 0, 5);

        return new SignalSummary
        {
            Overall = overall,
            Score = LinkScore(main),
            NrScore = LinkScore(nr),
            LteScore = LinkScore(lte),
            Bars = bars,
            Network = NetworkLabel(Raw.Str(data.GetValueOrDefault("network_type_str")).Trim()),
            Band = band,
            Operator = Raw.Str(data.GetValueOrDefault("network_operator")).Trim(),
            Lte = lte,
            Nr = nr,
            MainIs5G = ReferenceEquals(main, nr),
            MainRsrp = main.Values[Metric.Rsrp],
            MainSinr = main.Values[Metric.Sinr],
        };
    }

    // ---------- plain-language help shown in the UI ----------

    public sealed record MetricInfo(
        double ChartMin,
        double ChartMax,
        int MinSpan,
        string Title,
        string Unit,
        double ScaleMin,
        double ScaleMax,
        string EndsLow,
        string EndsHigh,
        string Help);

    public static readonly IReadOnlyDictionary<Metric, MetricInfo> Metrics = new Dictionary<Metric, MetricInfo>
    {
        [Metric.Rsrp] = new(
            ChartMin: -120,
            ChartMax: -70,
            MinSpan: 12,
            Title: "Signal strength",
            Unit: "dBm",
            ScaleMin: -125,
            ScaleMax: -65,
            EndsLow: "weak",
            EndsHigh: "strong",
            Help: "How strong the signal from the tower is when it reaches the router.\n"
                + "Numbers are negative: closer to 0 is stronger (-75 beats -105)."),
        [Metric.Rsrq] = new(
            ChartMin: -25,
            ChartMax: -5,
            MinSpan: 6,
            Title: "Signal quality",
            Unit: "dB",
            ScaleMin: -25,
            ScaleMax: -5,
            EndsLow: "poor",
            EndsHigh: "clean",
            Help: "How clean the signal is, taking into account how busy the tower is.\n"
                + "Closer to 0 is better."),
        [Metric.Sinr] = new(
            ChartMin: -5,
            ChartMax: 30,
            MinSpan: 8,
            Title: "Signal vs. noise",
            Unit: "dB",
            ScaleMin: -5,
            ScaleMax: 30,
            EndsLow: "noisy",
            EndsHigh: "clear",
            Help: "How much the signal stands out from interference and noise.\n"
                + "Higher is better. This one affects your speed the most."),
    };

    public static readonly IReadOnlyDictionary<string, string> Verdicts = new Dictionary<string, string>
    {
        [Excellent] = "Great signal, full speed",
        [Good] = "Solid signal, good speeds",
        [Fair] = "Usable, speed may drop",
        [Poor] = "Weak signal, try moving the router",
    };

    public const string NoDataVerdict = "No signal data yet";

    public static readonly IReadOnlyDictionary<string, string> Plain = new Dictionary<string, string>
    {
        [Excellent] = "Great for 4K streaming and gaming",
        [Good] = "Good for HD video and video calls",
        [Fair] = "OK for browsing; video may buffer",
        [Poor] = "Slow and unstable: try moving the router",
    };

    public const string NoDataPlain = "Waiting for the router…";

    public static string Verdict(string? rating)
    {
        if (rating is not null && Verdicts.TryGetValue(rating, out var text))
        {
            return text;
        }

        return NoDataVerdict;
    }

    public static string PlainText(string? rating)
    {
        if (rating is not null && Plain.TryGetValue(rating, out var text))
        {
            return text;
        }

        return NoDataPlain;
    }

    public static string ThresholdText(Metric metric)
    {
        var (excellentMin, goodMin, fairMin) = Thresholds[metric];
        var unit = Metrics[metric].Unit;
        return $"Excellent  ≥ {Num(excellentMin)} {unit}\n"
            + $"Good       {Num(goodMin)} to {Num(excellentMin)}\n"
            + $"Fair       {Num(fairMin)} to {Num(goodMin)}\n"
            + $"Poor       below {Num(fairMin)}";
    }

    /// <summary>Which of RSRP / SINR holds the overall rating down (null if they tie or are missing).</summary>
    public static Metric? WeakestMetric(Link link)
    {
        var strength = link.Ratings[Metric.Rsrp];
        var noise = link.Ratings[Metric.Sinr];
        if (strength is null || noise is null || strength == noise)
        {
            return null;
        }

        return RatingIndex(strength) > RatingIndex(noise) ? Metric.Rsrp : Metric.Sinr;
    }

    /// <summary>Python's <c>f"{value:g}"</c> for the threshold values.</summary>
    private static string Num(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}

/// <summary>One link (4G or 5G): values per metric, the ratings and whether data is present.</summary>
public sealed class Link
{
    private static readonly Metric[] AllMetrics = { Metric.Rsrp, Metric.Rsrq, Metric.Sinr };

    public Link(IReadOnlyDictionary<Metric, double?> values)
    {
        var complete = new Dictionary<Metric, double?>();
        foreach (var metric in AllMetrics)
        {
            complete[metric] = values.TryGetValue(metric, out var value) ? value : null;
        }

        Values = complete;
        Ratings = complete.ToDictionary(
            pair => pair.Key,
            pair => ControlSpawn.Router.Ratings.Rate(pair.Key, pair.Value));
        Present = complete[Metric.Rsrp] is not null || complete[Metric.Sinr] is not null;
    }

    public static Link Empty() => new(new Dictionary<Metric, double?>());

    public IReadOnlyDictionary<Metric, double?> Values { get; }

    public IReadOnlyDictionary<Metric, string?> Ratings { get; }

    public bool Present { get; }
}

/// <summary>The summary the UI draws from one signal reply.</summary>
public sealed class SignalSummary
{
    public string? Overall { get; init; }

    public double? Score { get; init; }

    public double? NrScore { get; init; }

    public double? LteScore { get; init; }

    public int Bars { get; init; }

    public string Network { get; init; } = "";

    public string Band { get; init; } = "";

    public string Operator { get; init; } = "";

    public Link Lte { get; init; } = Link.Empty();

    public Link Nr { get; init; } = Link.Empty();

    public bool MainIs5G { get; init; }

    public double? MainRsrp { get; init; }

    public double? MainSinr { get; init; }
}
