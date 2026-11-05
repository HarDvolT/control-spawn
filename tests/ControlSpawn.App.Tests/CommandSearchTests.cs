using ControlSpawn.App.Navigation;

namespace ControlSpawn.App.Tests;

public class CommandSearchTests
{
    private readonly CommandSearch _search = new();

    [Fact]
    public void Fan_mode_command_wins_for_fan_turbo()
    {
        var results = _search.Search("fan turbo");
        Assert.NotEmpty(results);
        Assert.Equal(SearchAction.FanMode, results[0].Action);
        Assert.Equal("Turbo", results[0].Key);
        Assert.Contains("Fan mode", results[0].Title);
    }

    [Fact]
    public void Scene_night_is_found()
    {
        var results = _search.Search("scene night");
        Assert.NotEmpty(results);
        Assert.Equal(SearchAction.Scene, results[0].Action);
        Assert.Equal("Night", results[0].Key);
    }

    [Fact]
    public void Partial_page_name_navigates()
    {
        var results = _search.Search("dash");
        Assert.Equal(SearchAction.Navigate, results[0].Action);
        Assert.Equal(nameof(PageKey.Dashboard), results[0].Key);
    }

    [Fact]
    public void Lights_off_is_a_command()
    {
        var results = _search.Search("lights off");
        Assert.Equal(SearchAction.LightsOff, results[0].Action);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var lower = _search.Search("monitor");
        var upper = _search.Search("MONITOR");
        Assert.Equal(lower[0].Key, upper[0].Key);
        Assert.Equal(nameof(PageKey.Monitor), lower[0].Key);
    }

    [Fact]
    public void Empty_query_lists_pages()
    {
        var results = _search.Search("   ");
        Assert.True(results.Count >= 8);
        Assert.All(results, r => Assert.Equal(SearchAction.Navigate, r.Action));
        Assert.Equal(nameof(PageKey.Dashboard), results[0].Key);
    }

    [Fact]
    public void Limit_is_respected()
    {
        Assert.Equal(3, _search.Search("", 3).Count);
        Assert.Equal(2, _search.Search("e", 2).Count);
        Assert.Empty(_search.Search("zzzz-nothing", 8));
    }

    [Fact]
    public void Widgets_toggle_is_offered()
    {
        var show = _search.Search("show widgets");
        Assert.Equal(SearchAction.ToggleWidgets, show[0].Action);
        Assert.Equal("show", show[0].Key);
        var hide = _search.Search("hide widgets");
        Assert.Equal("hide", hide[0].Key);
    }

    [Fact]
    public void Every_page_is_searchable_by_its_name()
    {
        foreach (var page in Pages.All)
        {
            var results = _search.Search(page.Title);
            Assert.Contains(results, r => r.Action == SearchAction.Navigate && r.Key == page.Key.ToString());
        }
    }
}
