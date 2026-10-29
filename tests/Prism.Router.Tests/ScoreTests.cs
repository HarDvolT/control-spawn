namespace Prism.Router.Tests;

public class ScoreTests
{
    [Fact]
    public void ThresholdsLandOnBandEdges()
    {
        Assert.Equal(40.0, Ratings.MetricScore(Metric.Rsrp, -100));
        Assert.Equal(60.0, Ratings.MetricScore(Metric.Rsrp, -90));
        Assert.Equal(80.0, Ratings.MetricScore(Metric.Rsrp, -80));
        Assert.Equal(40.0, Ratings.MetricScore(Metric.Sinr, 0));
        Assert.Equal(60.0, Ratings.MetricScore(Metric.Sinr, 13));
        Assert.Equal(80.0, Ratings.MetricScore(Metric.Sinr, 20));
    }

    [Fact]
    public void Clamped()
    {
        Assert.Equal(0.0, Ratings.MetricScore(Metric.Rsrp, -140));
        Assert.Equal(100.0, Ratings.MetricScore(Metric.Rsrp, -50));
        Assert.Equal(0.0, Ratings.MetricScore(Metric.Sinr, -20));
        Assert.Equal(100.0, Ratings.MetricScore(Metric.Sinr, 45));
        Assert.Null(Ratings.MetricScore(Metric.Sinr, null));
    }

    [Fact]
    public void WordAlwaysMatchesRating()
    {
        for (var value = -130; value < -60; value++)
        {
            Assert.Equal(
                Ratings.Rate(Metric.Rsrp, value),
                Ratings.ScoreRating(Ratings.MetricScore(Metric.Rsrp, value)));
        }

        for (var step = -20; step < 70; step++)
        {
            var value = step / 2.0;
            Assert.Equal(
                Ratings.Rate(Metric.Sinr, value),
                Ratings.ScoreRating(Ratings.MetricScore(Metric.Sinr, value)));
        }
    }

    [Fact]
    public void SummaryScoreIsWeakerOfMainLink()
    {
        var summary = Ratings.Summarize(D.Map(
            ("RSRP_5G", "-85"),
            ("SINR_5G", "8"),
            ("RSRP", "-70"),
            ("SINR", "25")));
        Assert.Equal(summary.Score, summary.NrScore);
        Assert.Equal(summary.Overall, Ratings.ScoreRating(summary.Score));
        Assert.Equal(Ratings.Fair, summary.Overall); // SINR 8 Fair beats RSRP -85 Good
        Assert.Equal(Ratings.Excellent, Ratings.ScoreRating(summary.LteScore));
        Assert.Null(Ratings.Summarize(new Dictionary<string, object?>()).Score);
    }

    [Fact]
    public void Bands()
    {
        Assert.Equal(Ratings.Excellent, Ratings.ScoreRating(100));
        Assert.Equal(Ratings.Good, Ratings.ScoreRating(79.9));
        Assert.Equal(Ratings.Fair, Ratings.ScoreRating(40));
        Assert.Equal(Ratings.Poor, Ratings.ScoreRating(0));
        Assert.Null(Ratings.ScoreRating(null));
    }
}

public class TrendTests
{
    [Fact]
    public void Directions()
    {
        Assert.Equal("up", Ratings.Trend(
            new (double, double?)[] { (0, 40), (5, 44), (10, 48), (15, 52) }, 15));
        Assert.Equal("down", Ratings.Trend(
            new (double, double?)[] { (0, 60), (5, 55), (10, 50) }, 10));
        Assert.Equal("steady", Ratings.Trend(
            new (double, double?)[] { (0, 50), (5, 52), (10, 49), (15, 51) }, 15));
    }

    [Fact]
    public void NeedsEnoughReadings()
    {
        Assert.Null(Ratings.Trend(new (double, double?)[] { (0, 40), (2, 60) }, 2));
        Assert.Null(Ratings.Trend(new (double, double?)[] { (0, 40), (1, 50), (2, 60) }, 2)); // under 5 s of data
        Assert.Null(Ratings.Trend(new (double, double?)[] { (0, 40), (5, 50), (10, 60) }, 100)); // all older than 30 s
    }

    [Fact]
    public void IgnoresMissing()
    {
        Assert.Equal("up", Ratings.Trend(
            new (double, double?)[] { (0, 40), (5, null), (10, 50), (15, 60) }, 15));
    }
}
