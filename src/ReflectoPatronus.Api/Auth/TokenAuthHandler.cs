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
    public const string ClientClaim = "cid";     // present when the caller is an API client (OAuth client credentials), not a person
    public const string ScopeClaim = "scope";    // the permissions an API token was narrowed to
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
        {
            await r.CloseAsync();
            return await ClientToken(hash);
        }

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

    /// <summary>An OAuth client-credentials token (api_token): acts as the client's service account, with the client's group permissions
    /// (narrowed to the token's scope, if it asked for fewer). Never bound to a device certificate.</summary>
    async Task<AuthenticateResult> ClientToken(byte[] hash)
    {
        await using var cmd = db.CreateCommand("""
            SELECT u.id, u.code, c.id, t.scope
            FROM api_token t JOIN api_client c ON c.id = t.client_id JOIN app_user u ON u.id = c.user_id
            WHERE t.access_hash = $1 AND t.expires_at > now() AND u.active AND (c.expires_at IS NULL OR c.expires_at > now())
            """);
        cmd.Parameters.AddWithValue(hash);
        await using var r = await cmd.ExecuteReaderAsync(Context.RequestAborted);
        if (!await r.ReadAsync(Context.RequestAborted))
            return AuthenticateResult.Fail("Invalid or expired token.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, r.GetInt16(0).ToString()),
            new(ClaimTypes.Name, r.GetString(1)),
            new(AuthDefaults.ClientClaim, r.GetGuid(2).ToString()),
        };
        if (!r.IsDBNull(3)) claims.Add(new Claim(AuthDefaults.ScopeClaim, r.GetString(3)));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
