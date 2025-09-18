using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Implementations;

public class ThemeService : IThemeService
{
    private bool _dark = true;
    public event EventHandler<bool>? DarkModeChanged;

    public Task<bool> GetDarkModeAsync() => Task.FromResult(_dark);

    public Task SaveThemeSettingsAsync(ThemeSettings settings)
    { _dark = settings.DarkMode; DarkModeChanged?.Invoke(this, _dark); return Task.CompletedTask; }

    public Task SetDarkModeAsync(bool isDark)
    { _dark = isDark; DarkModeChanged?.Invoke(this, _dark); return Task.CompletedTask; }

    public Task<ThemeSettings> GetThemeSettingsAsync() => Task.FromResult(new ThemeSettings { DarkMode = _dark });
}
