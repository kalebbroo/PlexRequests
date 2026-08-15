using PlexRequestsHosted.Services.Implementations;
using Xunit;

namespace PlexRequests.Tests;

public sealed class PlexDashboardParsingTests
{
    [Fact]
    public void LibrarySections_DoNotTreatMissingSectionSizeAsZeroItems()
    {
        const string sections = """
            {
              "MediaContainer": {
                "size": 1,
                "Directory": [{
                  "key": "3",
                  "type": "movie",
                  "title": "Movies",
                  "refreshing": false,
                  "scannedAt": 1786737600
                }]
              }
            }
            """;

        var library = Assert.Single(PlexApiService.ParseLibrarySections("application/json", sections));

        Assert.Null(library.ItemCount);
        Assert.Equal("movies", library.ItemLabel);
        Assert.Equal("3", library.Key);
        Assert.NotNull(library.LastScannedAt);
    }

    [Fact]
    public void ContentContainer_UsesTotalSizeAndParsesRecentArtwork()
    {
        const string content = """
            {
              "MediaContainer": {
                "size": 1,
                "totalSize": 55,
                "Metadata": [{
                  "title": "Example",
                  "year": 2026,
                  "thumb": "/library/metadata/12/thumb/1",
                  "addedAt": 1786737600
                }]
              }
            }
            """;

        var page = PlexApiService.ParseContainerPage("application/json", content, null);

        Assert.Equal(55, page.TotalSize);
        var item = Assert.Single(page.Items);
        Assert.Equal("Example", item.Title);
        Assert.Equal("/library/metadata/12/thumb/1", item.ThumbPath);
    }

    [Fact]
    public void Sessions_ParseArtworkViewerDeviceAndDirectStream()
    {
        const string sessions = """
            {
              "MediaContainer": {
                "Metadata": [{
                  "title": "Episode title",
                  "grandparentTitle": "Example Show",
                  "parentIndex": 2,
                  "index": 4,
                  "duration": 3600000,
                  "viewOffset": 900000,
                  "grandparentThumb": "/library/metadata/10/thumb/1",
                  "User": { "title": "Viewer", "thumb": "https://plex.tv/users/1/avatar" },
                  "Player": { "title": "Living Room", "device": "TV", "platform": "Roku", "state": "playing" },
                  "Session": { "bandwidth": 8200, "location": "lan" },
                  "Media": [{ "videoDecision": "copy", "audioDecision": "directplay" }]
                }]
              }
            }
            """;

        var session = Assert.Single(PlexApiService.ParseSessions("application/json", sessions));

        Assert.Equal("Example Show", session.Title);
        Assert.Equal("S02 E04 · Episode title", session.Subtitle);
        Assert.Equal("Viewer", session.Username);
        Assert.Equal("Direct Stream", session.PlaybackMode);
        Assert.Equal(25, session.ProgressPercent);
        Assert.Equal("Living Room", session.PlayerName);
        Assert.Equal(8200, session.BitrateKbps);
    }
}
