namespace PlexRequestsHosted.Infrastructure.Capture;

public sealed class FirefoxCaptureOptions
{
    public const string Section = "FirefoxCapture";

    public bool Enabled { get; set; } = true;
    public int PairingLifetimeMinutes { get; set; } = 10;
    public int DeviceLifetimeDays { get; set; } = 90;
    public int DeviceRenewalWindowDays { get; set; } = 30;
    public int MaxBatchSize { get; set; } = 250;

    /// <summary>Rendered page domains permitted for each implementation. Tokens remain source-scoped.</summary>
    public Dictionary<string, string[]> AllowedHostsByIndexer { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1337x"] =
        [
            "1337x.to",
            "1337x.st",
            "1337x.ws",
            "1337x.eu",
            "1337x.se",
            "1337x.so",
            "1337x.is"
        ],
        ["ext.to"] = ["ext.to"]
    };
}
