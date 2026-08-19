using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Media;

/// <summary>One provider-neutral query builder shared by every free-text indexer adapter.</summary>
public static class AcquisitionQuery
{
    public static string Build(FulfillmentJobDto job, bool includeMovieYear = true)
    {
        if (job.MediaType == MediaType.Music)
            return job.Music?.BuildSearchText(job.Title) ?? job.Title;
        return includeMovieYear && job.MediaType == MediaType.Movie && job.Year is int year
            ? $"{job.Title} {year}"
            : job.Title;
    }
}
