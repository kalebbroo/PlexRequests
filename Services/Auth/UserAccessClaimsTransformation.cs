using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Auth;

/// <summary>
/// Replaces long-lived cookie role claims with the user's current database access on every HTTP
/// authentication. This closes the window where a demoted administrator could keep using protected APIs
/// until an eight-hour cookie expired.
/// </summary>
public sealed class UserAccessClaimsTransformation(
    IServiceScopeFactory scopeFactory,
    ILogger<UserAccessClaimsTransformation> logger) : IClaimsTransformation
{
    internal const string RefreshedClaim = "plex_access_refreshed";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(x => x.Type == RefreshedClaim))
            return principal;
        if (!int.TryParse(principal.FindFirst("user_id")?.Value, out var userId) || userId <= 0)
            return principal;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var access = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
            var snapshot = await access.GetAccessAsync(userId);
            return RefreshRoles(principal, snapshot.IsAdmin, addMarker: true);
        }
        catch (Exception ex)
        {
            // A transient database failure must not sign a valid user out. Mutation services still perform
            // their own database authorization checks, and the next request will retry this transformation.
            logger.LogWarning(ex, "Could not refresh access claims for user {UserId}", userId);
            return principal;
        }
    }

    internal static ClaimsPrincipal RefreshRoles(ClaimsPrincipal principal, bool isAdmin, bool addMarker = false)
    {
        var source = principal.Identities.FirstOrDefault(x => x.IsAuthenticated);
        if (source is null) return principal;

        var identities = principal.Identities.Select(identity =>
        {
            // Strip roles from every identity so an unexpected secondary identity cannot preserve a stale
            // administrator claim. Only the primary authenticated identity receives current roles.
            var claims = identity.Claims
                .Where(x => x.Type != identity.RoleClaimType && x.Type != RefreshedClaim)
                .ToList();
            if (ReferenceEquals(identity, source))
            {
                claims.Add(new Claim(identity.RoleClaimType, "User"));
                if (isAdmin) claims.Add(new Claim(identity.RoleClaimType, "Admin"));
                if (addMarker) claims.Add(new Claim(RefreshedClaim, "true"));
            }
            return new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
        });
        return new ClaimsPrincipal(identities);
    }
}
