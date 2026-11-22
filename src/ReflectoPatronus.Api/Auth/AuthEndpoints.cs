using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;

namespace ReflectoPatronus.Api.Auth;

public record LoginRequest(string UserCode, string Password, string DeviceName, string Platform);
public record RefreshRequest(string RefreshToken);
public record UserInfo(int Id, string Code, string? Name, string TimeZone);
public record TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds, UserInfo User);
public record SessionInfo(Guid Id, string DeviceName, string Platform, DateTime CreatedAt, DateTime LastSeenAt, bool Current);

public static class AuthEndpoints
{
    const int MaxFailures = 5;
    static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);
    static readonly HashSet<string> Platforms = ["web", "desktop", "ios", "ipados", "other"];
    static readonly PasswordHasher<object> Hasher = new();
    // verified against when the user does not exist, so timing does not reveal which user codes are valid
    static readonly string DummyHash = Hasher.HashPassword(new object(), "not-a-real-password");

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapPost("/login", Login).AllowAnonymous();
        auth.MapPost("/refresh", Refresh).AllowAnonymous();
        auth.MapPost("/logout", Logout);

        var me = api.MapGroup("/me");
        me.MapGet("/sessions", ListSessions);
        me.MapDelete("/sessions/{id:guid}", RevokeSession);
    }

    static async Task<IResult> Login(LoginRequest req, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.UserCode) || string.IsNullOrEmpty(req.Password)
            || string.IsNullOrWhiteSpace(req.DeviceName) || !Platforms.Contains(req.Platform))
            return Results.BadRequest(new { error = "userCode, password, deviceName and a valid platform are required." });

        await using var cmd = db.CreateCommand("""
            SELECT u.id, u.name, u.password_hash, u.locked_until,
                   coalesce(u.time_zone, c.time_zone, 'America/Chicago')
            FROM app_user u LEFT JOIN LATERAL (SELECT time_zone FROM company ORDER BY id LIMIT 1) c ON true
            WHERE u.code = $1 AND u.active
            """);
        cmd.Parameters.AddWithValue(req.UserCode.Trim());
        short? userId = null; string? name = null, hash = null, tz = null; DateTime? lockedUntil = null;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
            {
                userId = r.GetInt16(0);
                name = r.IsDBNull(1) ? null : r.GetString(1);
                hash = r.IsDBNull(2) ? null : r.GetString(2);
                lockedUntil = r.IsDBNull(3) ? null : r.GetDateTime(3);
                tz = r.GetString(4);
            }

        var verdict = Hasher.VerifyHashedPassword(new object(), hash ?? DummyHash, req.Password);
        var ok = userId is not null && hash is not null && verdict != PasswordVerificationResult.Failed;
        var locked = lockedUntil is { } l && l > DateTime.UtcNow;
        if (!ok || locked)
        {
            if (userId is not null && !locked)
            {
                await using var f = db.CreateCommand("""
                    UPDATE app_user SET failed_logins = failed_logins + 1,
                        locked_until = CASE WHEN failed_logins + 1 >= $2 THEN now() + interval '15 minutes' ELSE locked_until END
                    WHERE id = $1
                    """);
                f.Parameters.AddWithValue(userId.Value);
                f.Parameters.AddWithValue((short)MaxFailures);
                await f.ExecuteNonQueryAsync(ct);
            }
            // same response for unknown user, bad password and lockout
            return Results.Json(new { error = "Invalid credentials." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await using var rh = db.CreateCommand("UPDATE app_user SET password_hash = $2 WHERE id = $1");
            rh.Parameters.AddWithValue(userId!.Value);
            rh.Parameters.AddWithValue(Hasher.HashPassword(new object(), req.Password));
            await rh.ExecuteNonQueryAsync(ct);
        }
        await using (var reset = db.CreateCommand("UPDATE app_user SET failed_logins = 0, locked_until = NULL WHERE id = $1"))
        {
            reset.Parameters.AddWithValue(userId!.Value);
            await reset.ExecuteNonQueryAsync(ct);
        }

        var access = Tokens.New(); var refresh = Tokens.New();
        await using var ins = db.CreateCommand("""
            INSERT INTO device_session (user_id, platform, device_name, access_hash, access_expires_at, refresh_hash, refresh_expires_at, cert_fingerprint)
            VALUES ($1, $2, $3, $4, now() + $5, $6, now() + $7, $8)
            """);
        ins.Parameters.AddWithValue(userId!.Value);
        ins.Parameters.AddWithValue(req.Platform);
        ins.Parameters.AddWithValue(req.DeviceName.Trim());
        ins.Parameters.AddWithValue(Tokens.Hash(access));
        ins.Parameters.AddWithValue(AccessLifetime);
        ins.Parameters.AddWithValue(Tokens.Hash(refresh));
        ins.Parameters.AddWithValue(RefreshLifetime);
        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)GatewayGuard.Fingerprint(http) ?? DBNull.Value });
        await ins.ExecuteNonQueryAsync(ct);
        return Results.Ok(new TokenResponse(access, refresh, (int)AccessLifetime.TotalSeconds,
            new UserInfo(userId.Value, req.UserCode.Trim(), name, tz!)));
    }

    /// <summary>Rotates both tokens. The old refresh token stops working immediately.</summary>
    static async Task<IResult> Refresh(RefreshRequest req, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RefreshToken)) return Results.BadRequest(new { error = "refreshToken required." });
        var access = Tokens.New(); var refresh = Tokens.New();
        await using var cmd = db.CreateCommand("""
            UPDATE device_session s SET access_hash = $2, access_expires_at = now() + $3,
                   refresh_hash = $4, refresh_expires_at = now() + $5, last_seen_at = now()
            FROM app_user u
            WHERE s.refresh_hash = $1 AND s.revoked_at IS NULL AND s.refresh_expires_at > now()
              AND u.id = s.user_id AND u.active
              AND (s.cert_fingerprint IS NULL OR s.cert_fingerprint = $6)   -- bound sessions need the same device certificate
            RETURNING u.id, u.code, u.name,
                      coalesce(u.time_zone, (SELECT time_zone FROM company ORDER BY id LIMIT 1), 'America/Chicago')
            """);
        cmd.Parameters.AddWithValue(Tokens.Hash(req.RefreshToken));
        cmd.Parameters.AddWithValue(Tokens.Hash(access));
        cmd.Parameters.AddWithValue(AccessLifetime);
        cmd.Parameters.AddWithValue(Tokens.Hash(refresh));
        cmd.Parameters.AddWithValue(RefreshLifetime);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)GatewayGuard.Fingerprint(http) ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return Results.Json(new { error = "Invalid or expired refresh token." }, statusCode: StatusCodes.Status401Unauthorized);
        return Results.Ok(new TokenResponse(access, refresh, (int)AccessLifetime.TotalSeconds,
            new UserInfo(r.GetInt16(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3))));
    }

    static async Task<IResult> Logout(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        await Revoke(db, user.UserId(), user.SessionId(), ct);
        return Results.NoContent();
    }

    static async Task<IResult> ListSessions(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, device_name, platform, created_at, last_seen_at FROM device_session
            WHERE user_id = $1 AND revoked_at IS NULL AND refresh_expires_at > now() ORDER BY last_seen_at DESC
            """);
        cmd.Parameters.AddWithValue(user.UserId());
        var list = new List<SessionInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SessionInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetDateTime(3), r.GetDateTime(4),
                r.GetGuid(0) == user.SessionId()));
        return Results.Ok(list);
    }

    /// <summary>Users can only revoke their own sessions (e.g. a lost laptop).</summary>
    static async Task<IResult> RevokeSession(Guid id, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
        => await Revoke(db, user.UserId(), id, ct) ? Results.NoContent() : Results.NotFound();

    static async Task<bool> Revoke(NpgsqlDataSource db, short userId, Guid sessionId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("UPDATE device_session SET revoked_at = now() WHERE id = $1 AND user_id = $2 AND revoked_at IS NULL");
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public static short UserId(this ClaimsPrincipal p) => short.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static Guid SessionId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AuthDefaults.SessionClaim)!);

    /// <summary>First-run bootstrap: if NO user has a password yet, set one for Bootstrap:AdminUser (created if missing).</summary>
    public static async Task BootstrapAdminAsync(NpgsqlDataSource db, IConfiguration cfg, ILogger log)
    {
        var code = cfg["Bootstrap:AdminUser"]; var pw = cfg["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrEmpty(pw)) return;
        await using var any = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM app_user WHERE password_hash IS NOT NULL)");
        if ((bool)(await any.ExecuteScalarAsync())!) { log.LogInformation("Bootstrap admin skipped: a user already has a password."); return; }
        if (pw.Length < 12) { log.LogError("Bootstrap:AdminPassword must be at least 12 characters; admin NOT created."); return; }
        await using var cmd = db.CreateCommand("""
            INSERT INTO app_user (code, name, active, password_hash, is_admin) VALUES ($1, $1, true, $2, true)
            ON CONFLICT (code) DO UPDATE SET password_hash = EXCLUDED.password_hash, active = true, is_admin = true
            """);
        cmd.Parameters.AddWithValue(code.Trim());
        cmd.Parameters.AddWithValue(Hasher.HashPassword(new object(), pw));
        await cmd.ExecuteNonQueryAsync();
        await using (var grp = db.CreateCommand("INSERT INTO user_group (user_id, group_id) SELECT u.id, g.id FROM app_user u, app_group g WHERE u.code = $1 AND g.code = 'administrators' ON CONFLICT DO NOTHING"))
        { grp.Parameters.AddWithValue(code.Trim()); await grp.ExecuteNonQueryAsync(); }
        log.LogWarning("Bootstrap admin '{User}' created/updated. Remove Bootstrap__* settings now.", code.Trim());
    }
}
