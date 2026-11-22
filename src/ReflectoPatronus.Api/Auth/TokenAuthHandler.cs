using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ReflectoPatronus.Api.Auth;

public static class AuthDefaults
{
    public const string Scheme = "DeviceToken";
    public const string SessionClaim = "sid";
}

/// <summary>Validates "Authorization: Bearer &lt;token&gt;" against device_session (not revoked, not expired, user active).</summary>
sealed class TokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, NpgsqlDataSource db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var hash = Tokens.Hash(header[7..].Trim());

        await using var cmd = db.CreateCommand("""
            SELECT s.id, u.id, u.code, u.name, s.last_seen_at, s.cert_fingerprint
            FROM device_session s JOIN app_user u ON u.id = s.user_id
            WHERE s.access_hash = $1 AND s.revoked_at IS NULL AND s.access_expires_at > now() AND u.active
            """);
        cmd.Parameters.AddWithValue(hash);
        await using var r = await cmd.ExecuteReaderAsync(Context.RequestAborted);
        if (!await r.ReadAsync(Context.RequestAborted))
            return AuthenticateResult.Fail("Invalid or expired token.");

        var boundTo = r.IsDBNull(5) ? null : r.GetString(5);
        if (boundTo is not null && !string.Equals(boundTo, GatewayGuard.Fingerprint(Context), StringComparison.Ordinal))
            return AuthenticateResult.Fail("Token is bound to a different device certificate.");   // fails closed: also when no gateway headers are present

        var sessionId = r.GetGuid(0);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, r.GetInt16(1).ToString()),
            new(ClaimTypes.Name, r.GetString(2)),
            new(AuthDefaults.SessionClaim, sessionId.ToString()),
        };
        var stale = r.GetDateTime(4) < DateTime.UtcNow.AddMinutes(-1);
        await r.CloseAsync();
        if (stale)   // last-seen is informational: touch at most once a minute
        {
            await using var touch = db.CreateCommand("UPDATE device_session SET last_seen_at = now() WHERE id = $1");
            touch.Parameters.AddWithValue(sessionId);
            await touch.ExecuteNonQueryAsync(Context.RequestAborted);
        }
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
