namespace PlexRequestsHosted.Infrastructure.Capture;

public sealed class FirefoxCaptureOptions
{
    public const string Section = "FirefoxCapture";

    public bool Enabled { get; set; } = true;
    public int PairingLifetimeMinutes { get; set; } = 10;
    public int DeviceLifetimeDays { get; set; } = 90;
    public int MaxBatchSize { get; set; } = 250;

    /// <summary>Only rendered pages from these source domains may enter the catalog.</summary>
    public string[] AllowedHosts { get; set; } =
    [
        "1337x.to",
        "1337x.st",
        "1337x.ws",
        "1337x.eu",
        "1337x.se",
        "1337x.so",
        "1337x.is"
    ];
}
