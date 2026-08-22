using PlexRequestsHosted.Components.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MediaRequestFeedbackTests
{
    private static readonly MediaCardDto Series = new()
    {
        Title = "Example Show",
        MediaType = MediaType.TvShow
    };

    [Fact]
    public void Approved_monitored_series_confirms_ongoing_monitoring()
    {
        var result = new MediaRequestResult
        {
            Success = true,
            NewStatus = RequestStatus.Approved,
            RequestScope = RequestScopeKind.Series,
            MonitorsFutureReleases = true
        };

        Assert.Equal(
            "Example Show: full series requested. Future episodes will be monitored automatically.",
            MediaRequestFeedback.Success(Series, result));
    }

    [Fact]
    public void Pending_monitored_series_explains_that_monitoring_starts_after_approval()
    {
        var result = new MediaRequestResult
        {
            Success = true,
            NewStatus = RequestStatus.Pending,
            RequestScope = RequestScopeKind.Series,
            MonitorsFutureReleases = true
        };

        Assert.Equal(
            "Example Show: full series requested. Once approved, future episodes will be monitored automatically.",
            MediaRequestFeedback.Success(Series, result));
    }

    [Fact]
    public void Unmonitored_series_does_not_claim_future_episode_tracking()
    {
        var result = new MediaRequestResult
        {
            Success = true,
            NewStatus = RequestStatus.Approved,
            RequestScope = RequestScopeKind.Series
        };

        Assert.Equal("Example Show: full series requested.", MediaRequestFeedback.Success(Series, result));
    }
}
