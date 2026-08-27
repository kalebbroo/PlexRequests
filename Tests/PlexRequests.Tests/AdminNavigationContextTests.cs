using PlexRequestsHosted.Shared.Navigation;
using Xunit;

namespace PlexRequests.Tests;

public sealed class AdminNavigationContextTests
{
    [Theory]
    [InlineData(null, null, "overview", "")]
    [InlineData("requests", "approvals", "requests", "queue")]
    [InlineData("requests", "issues", "requests", "issues")]
    [InlineData("downloads", null, "release-settings", "basics")]
    [InlineData("downloads", "quality", "release-settings", "quality")]
    [InlineData("downloads", "assignment", "release-settings", "assignment")]
    [InlineData("downloads", "formats", "release-settings", "formats")]
    [InlineData("downloads", "indexers", "sources", "")]
    [InlineData("library", null, "libraries", "destinations")]
    [InlineData("library", "network", "libraries", "network")]
    [InlineData("library", "catalogs", "libraries", "catalogs")]
    [InlineData("jobs", null, "activity", "missing")]
    [InlineData("users", null, "users", "")]
    [InlineData("system", null, "maintenance", "")]
    public void Current_routes_resolve_to_task_oriented_workspaces(
        string? tab, string? sub, string destination, string section)
    {
        var result = AdminNavigationContext.Resolve(tab, sub);

        Assert.Equal(destination, result.Destination);
        Assert.Equal(section, result.Section);
    }

    [Theory]
    [InlineData("approvals", "requests", "queue")]
    [InlineData("issues", "requests", "issues")]
    [InlineData("quality", "release-settings", "quality")]
    [InlineData("assignment", "release-settings", "assignment")]
    [InlineData("formats", "release-settings", "formats")]
    [InlineData("indexers", "sources", "")]
    [InlineData("network", "libraries", "network")]
    [InlineData("catalogs", "libraries", "catalogs")]
    public void Legacy_single_tab_links_remain_valid(string tab, string destination, string section)
    {
        var result = AdminNavigationContext.Resolve(tab, null);

        Assert.Equal(destination, result.Destination);
        Assert.Equal(section, result.Section);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData(" ")]
    public void Unknown_routes_fail_safe_to_overview(string tab)
        => Assert.Equal("overview", AdminNavigationContext.Resolve(tab, null).Destination);
}
