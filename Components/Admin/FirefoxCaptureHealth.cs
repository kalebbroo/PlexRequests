using MudBlazor;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Components.Admin;

internal enum FirefoxCaptureHealthState
{
    NotPaired,
    UpdateRequired,
    Offline,
    CapturePaused,
    NeedsAttention,
    ChallengePaused,
    CatchingUp,
    Healthy
}

internal static class FirefoxCaptureHealth
{
    public static FirefoxCaptureHealthState State(FirefoxCaptureDeviceDto? device, DateTime now) => device switch
    {
        null => FirefoxCaptureHealthState.NotPaired,
        { LastHeartbeatAt: null } => FirefoxCaptureHealthState.UpdateRequired,
        _ when device.LastHeartbeatAt < now.AddMinutes(-3) => FirefoxCaptureHealthState.Offline,
        _ when device.UpdateAvailable => FirefoxCaptureHealthState.UpdateRequired,
        _ when !device.CaptureEnabled => FirefoxCaptureHealthState.CapturePaused,
        { FailedUploads: > 0 } or { AttentionDetails: > 0 } => FirefoxCaptureHealthState.NeedsAttention,
        _ when device.HydrationPausedUntil > now => FirefoxCaptureHealthState.ChallengePaused,
        { QueuedUploads: > 0 } or { PendingDetails: > 0 } => FirefoxCaptureHealthState.CatchingUp,
        _ => FirefoxCaptureHealthState.Healthy
    };

    public static Color Color(FirefoxCaptureHealthState state) => state switch
    {
        FirefoxCaptureHealthState.Healthy => MudBlazor.Color.Success,
        FirefoxCaptureHealthState.CatchingUp or FirefoxCaptureHealthState.UpdateRequired => MudBlazor.Color.Info,
        FirefoxCaptureHealthState.NeedsAttention => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Warning
    };

    public static string Icon(FirefoxCaptureHealthState state) => state switch
    {
        FirefoxCaptureHealthState.NotPaired => Icons.Material.Filled.ExtensionOff,
        FirefoxCaptureHealthState.UpdateRequired => Icons.Material.Filled.Update,
        FirefoxCaptureHealthState.Offline => Icons.Material.Filled.CloudOff,
        FirefoxCaptureHealthState.CapturePaused or FirefoxCaptureHealthState.ChallengePaused =>
            Icons.Material.Filled.PauseCircle,
        FirefoxCaptureHealthState.NeedsAttention => Icons.Material.Filled.ErrorOutline,
        FirefoxCaptureHealthState.CatchingUp => Icons.Material.Filled.Sync,
        _ => Icons.Material.Filled.CheckCircle
    };

    public static string Label(FirefoxCaptureHealthState state) => state switch
    {
        FirefoxCaptureHealthState.NotPaired => "pair Firefox",
        FirefoxCaptureHealthState.UpdateRequired => "update needed",
        FirefoxCaptureHealthState.Offline => "Firefox offline",
        FirefoxCaptureHealthState.CapturePaused => "capture paused",
        FirefoxCaptureHealthState.NeedsAttention => "needs attention",
        FirefoxCaptureHealthState.ChallengePaused => "challenge paused",
        FirefoxCaptureHealthState.CatchingUp => "catching up",
        _ => "healthy"
    };
}
