using MudBlazor;
using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Implementations;

public class ToastService(ISnackbar snackbar) : IToastService
{
    private readonly ISnackbar _snackbar = snackbar;

    public Task ShowErrorAsync(string message, string? title = null)
    { _snackbar.Add(message, Severity.Error); return Task.CompletedTask; }

    public Task ShowInfoAsync(string message, string? title = null)
    { _snackbar.Add(message, Severity.Info); return Task.CompletedTask; }

    public Task ShowSuccessAsync(string message, string? title = null)
    { _snackbar.Add(message, Severity.Success); return Task.CompletedTask; }

    public Task ShowWarningAsync(string message, string? title = null)
    { _snackbar.Add(message, Severity.Warning); return Task.CompletedTask; }
}
