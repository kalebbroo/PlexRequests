using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class AnimeCompactQueryTests
{
    [Fact]
    public void Anime_searches_the_compact_title_as_well_as_the_dotted_one()
    {
        var job = new FulfillmentJobDto { Title = "D.N.Angel", MediaType = MediaType.TvShow, IsAnime = true, Id = 1 };

        var queries = AcquisitionQuery.BuildScoped(job);

        Assert.Equal("D.N.Angel", queries[0]);
        Assert.Contains("DNAngel", queries);
    }

    [Fact]
    public void A_title_without_punctuation_adds_no_extra_query()
    {
        var job = new FulfillmentJobDto { Title = "Severance", MediaType = MediaType.TvShow, IsAnime = true, Id = 1 };

        Assert.DoesNotContain(AcquisitionQuery.BuildScoped(job), query => query == "SeveranceX");
        Assert.Single(AcquisitionQuery.BuildScoped(job, maxScopes: 0));
        Assert.Equal(1, AcquisitionQuery.BuildScoped(job).Count(query => query == "Severance"));
    }
}
