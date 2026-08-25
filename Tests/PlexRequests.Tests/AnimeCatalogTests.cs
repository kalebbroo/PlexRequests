using PlexRequestsHosted.Services.Implementations;
using Xunit;

namespace PlexRequests.Tests;

public class AnimeCatalogTests
{
    [Fact]
    public void Candidate_RequiresAnimationAndJapaneseOriginSignal()
    {
        Assert.True(TmdbMetadataProvider.IsAnimeCandidate([16, 10759], "ja", null));
        Assert.True(TmdbMetadataProvider.IsAnimeCandidate([16], "en", ["JP"]));

        Assert.False(TmdbMetadataProvider.IsAnimeCandidate([16, 10762], "en", ["US"]));
        Assert.False(TmdbMetadataProvider.IsAnimeCandidate([18], "ja", ["JP"]));
    }

    [Fact]
    public void Candidate_AcceptsCountrySignalWhenLanguageMetadataIsMissing()
    {
        Assert.True(TmdbMetadataProvider.IsAnimeCandidate([16], null, ["Japan"]));
        Assert.False(TmdbMetadataProvider.IsAnimeCandidate([16], null, null));
    }
}
