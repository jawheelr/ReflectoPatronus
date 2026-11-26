using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using NpgsqlTypes;

namespace ReflectoPatronus.Api.Auth;

public record ApiClientInput(string? Name, string? Note, IReadOnlyList<int>? GroupIds, bool Active = true, DateTime? ExpiresAt = null);
public record ApiClientRow(Guid Id, string ClientId, string Name, string? Note, bool Active, IReadOnlyList<int> GroupIds, DateTime CreatedAt, DateTime SecretRotatedAt,
    DateTime? ExpiresAt, DateTime? LastUsedAt, int Tokens);

/// <summary>
/// OAuth 2.0 client credentials for other software (a website, a purchasing system, a script).
/// An API client is a service account: it cannot sign in, and it can do exactly what its groups allow, minus anything a token asks to drop.
/// POST /api/v1/auth/token exchanges client_id + client_secret for a one-hour bearer token (RFC 6749 section 4.4).
/// Managing clients needs users.manage; clients can never be members of a group that holds '*' or users.manage.
/// </summary>
public static class ApiClients
{
    static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/auth/token", Token).AllowAnonymous();
        var g = api.MapGroup("/api-clients").RequirePermission("users.manage");
        g.MapGet("", List); g.MapPost("", Create); g.MapPut("/{id:guid}", Update); g.MapPost("/{id:guid}/secret", Rotate); g.MapDelete("/{id:guid}", Delete);
    }

    static string NewClientId() => "rpc_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(12));
    static string NewSecret() => "rps_" + Tokens.New();
    static IResult OAuthError(string error, string description, int status = 400)
    {
        var r = Results.Json(new { error, error_description = description }, statusCode: status);
        return status == 401 ? new WithHeader(r, "WWW-Authenticate", "Basic realm=\"ReflectoPatronus\"") : r;
    }
    sealed class WithHeader(IResult inner, string name, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx) { ctx.Response.Headers[name] = value; return inner.ExecuteAsync(ctx); }
    }

    // ---- the token endpoint -------------------------------------------------------------------------------------------------

    /// <summary>grant_type=client_credentials. Credentials come from HTTP Basic or from client_id / client_secret in the form body.
    /// Optional "scope": a space-separated list of permission keys to narrow this token to (must be ones the client has).</summary>
    static async Task<IResult> Token(HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        string? grant = null, id = null, secret = null, scope = null;
        if (http.Request.HasFormContentType)
        {
            var f = await http.Request.ReadFormAsync(ct);
            grant = f["grant_type"]; id = f["client_id"]; secret = f["client_secret"]; scope = f["scope"];
        }
        else if (http.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
                string? S(string k) => doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                grant = S("grant_type"); id = S("client_id"); secret = S("client_secret"); scope = S("scope");
            }
            catch (System.Text.Json.JsonException) { return OAuthError("invalid_request", "The body is not valid JSON."); }
        }
        var basic = http.Request.Headers.Authorization.ToString();
        if (basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..].Trim()));
                var i = pair.IndexOf(':');
                if (i > 0) { id = Uri.UnescapeDataString(pair[..i]); secret = Uri.UnescapeDataString(pair[(i + 1)..]); }
            }
            catch (FormatException) { return OAuthError("invalid_client", "The Basic credentials are not valid.", 401); }
        }

        if (grant != "client_credentials") return OAuthError("unsupported_grant_type", "Only grant_type=client_credentials is supported.");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) return OAuthError("invalid_client", "client_id and client_secret are required.", 401);

        var hash = Tokens.Hash(secret);
        await using var cmd = db.CreateCommand("""
            SELECT c.id, c.secret_hash, u.id FROM api_client c JOIN app_user u ON u.id = c.user_id
            WHERE c.client_id = $1 AND u.active AND (c.expires_at IS NULL OR c.expires_at > now())
            """);
        cmd.Parameters.AddWithValue(id);
        Guid clientRow = default; byte[]? stored = null; short userId = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { clientRow = r.GetGuid(0); stored = (byte[])r[1]; userId = r.GetInt16(2); }
        // compare against something even for an unknown client so the response time does not say which client ids exist
        var ok = CryptographicOperations.FixedTimeEquals(stored ?? new byte[32], hash) && stored is not null;
        if (!ok) return OAuthError("invalid_client", "Unknown client, wrong secret, or the client is switched off or expired.", 401);

        string? narrowed = null;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var asked = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            var have = await Permissions.Load(db, userId, ct);
            var bad = asked.Where(a => !have.Contains(a)).ToArray();
            if (bad.Length > 0) return OAuthError("invalid_scope", "This client does not have: " + string.Join(", ", bad));
            narrowed = string.Join(' ', asked);
        }

        var token = Tokens.New();
        await using (var ins = db.CreateCommand("INSERT INTO api_token (access_hash, client_id, expires_at, scope) VALUES ($1, $2, $3, $4)"))
        {
            ins.Parameters.AddWithValue(Tokens.Hash(token)); ins.Parameters.AddWithValue(clientRow);
            ins.Parameters.AddWithValue(DateTime.UtcNow.Add(TokenLifetime)); ins.Parameters.Add(new NpgsqlParameter { Value = (object?)narrowed ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var up = db.CreateCommand("UPDATE api_client SET last_used_at = now() WHERE id = $1")) { up.Parameters.AddWithValue(clientRow); await up.ExecuteNonQueryAsync(ct); }
        await using (var gc = db.CreateCommand("DELETE FROM api_token WHERE expires_at < now() - interval '1 day'")) await gc.ExecuteNonQueryAsync(ct);

        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.Pragma = "no-cache";
        return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = (int)TokenLifetime.TotalSeconds, scope = narrowed });
    }

    // ---- management (users.manage) ----------------------------------------------------------------------------------------

    static async Task<IResult> List(NpgsqlDataSource db, CancellationToken ct)
    {
        var groups = new Dictionary<int, List<int>>();
        await using (var g = db.CreateCommand("SELECT ug.user_id, ug.group_id FROM user_group ug JOIN api_client c ON c.user_id = ug.user_id ORDER BY ug.group_id"))
        await using (var gr = await g.ExecuteReaderAsync(ct))
            while (await gr.ReadAsync(ct)) (groups.TryGetValue(gr.GetInt16(0), out var l) ? l : groups[gr.GetInt16(0)] = []).Add(gr.GetInt16(1));
        await using var c = db.CreateCommand("""
            SELECT c.id, c.client_id, c.name, c.note, u.active, u.id, c.created_at, c.secret_rotated_at, c.expires_at, c.last_used_at,
                   (SELECT count(*) FROM api_token t WHERE t.client_id = c.id AND t.expires_at > now())
            FROM api_client c JOIN app_user u ON u.id = c.user_id ORDER BY u.active DESC, lower(c.name)
            """);
        var rows = new List<ApiClientRow>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), groups.GetValueOrDefault(r.GetInt16(5)) ?? [],
                r.GetDateTime(6), r.GetDateTime(7), r.IsDBNull(8) ? null : r.GetDateTime(8), r.IsDBNull(9) ? null : r.GetDateTime(9), (int)r.GetInt64(10)));
        return Results.Ok(rows);
    }

    /// <summary>Groups must exist, and none may hold '*' (administrators) or users.manage (which could mint more credentials).</summary>
    static async Task<string?> CheckGroups(NpgsqlConnection cn, NpgsqlTransaction tx, IReadOnlyList<int>? ids, CancellationToken ct)
    {
        if (ids is null || ids.Count == 0) return "Choose at least one group: it decides what this client can do.";
        var distinct = ids.Distinct().Select(x => (short)x).ToArray();
        await using var c = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM app_group WHERE id = ANY($1)),
                   (SELECT count(*) FROM group_permission WHERE group_id = ANY($1) AND permission IN ('*', 'users.manage'))
            """, cn, tx);
        c.Parameters.AddWithValue(distinct);
        await using var r = await c.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        if (r.GetInt64(0) != distinct.Length) return "Unknown group.";
        if (r.GetInt64(1) > 0) return "An API client cannot belong to Administrators or any group that manages users.";
        return null;
    }
    static async Task SetGroups(NpgsqlConnection cn, NpgsqlTransaction tx, short userId, IReadOnlyList<int> ids, CancellationToken ct)
    {
        await using (var d = new NpgsqlCommand("DELETE FROM user_group WHERE user_id = $1", cn, tx)) { d.Parameters.AddWithValue(userId); await d.ExecuteNonQueryAsync(ct); }
        await using var i = new NpgsqlCommand("INSERT INTO user_group (user_id, group_id) SELECT $1, unnest($2::smallint[])", cn, tx);
        i.Parameters.AddWithValue(userId); i.Parameters.AddWithValue(ids.Distinct().Select(x => (short)x).ToArray()); await i.ExecuteNonQueryAsync(ct);
    }
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static async Task<IResult> Create(ApiClientInput i, System.Security.Claims.ClaimsPrincipal me, NpgsqlDataSource db, CancellationToken ct)
    {
        if (N(i.Name) is not { } name || name.Length > 60) return Bad("A name is required (up to 60 characters): say what uses this client.");
        if (i.ExpiresAt is { } ex && ex <= DateTime.UtcNow) return Bad("The expiry date must be in the future.");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        if (await CheckGroups(cn, tx, i.GroupIds, ct) is { } bad) return Bad(bad);

        short userId;
        await using (var u = new NpgsqlCommand("INSERT INTO app_user (code, name, active, is_service) VALUES ($1, $2, true, true) RETURNING id", cn, tx))
        {
            u.Parameters.AddWithValue("api-" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant().Replace('_', 'x').Replace('-', 'y'));
            u.Parameters.AddWithValue(name); userId = Convert.ToInt16(await u.ExecuteScalarAsync(ct));
        }
        await SetGroups(cn, tx, userId, i.GroupIds!, ct);
        var clientId = NewClientId(); var secret = NewSecret(); Guid id;
        await using (var c = new NpgsqlCommand("""
            INSERT INTO api_client (client_id, secret_hash, user_id, name, note, expires_at, created_by) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """, cn, tx))
        {
            c.Parameters.AddWithValue(clientId); c.Parameters.AddWithValue(Tokens.Hash(secret)); c.Parameters.AddWithValue(userId); c.Parameters.AddWithValue(name);
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)N(i.Note) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)i.ExpiresAt?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            c.Parameters.AddWithValue(me.UserId()); id = (Guid)(await c.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/api-clients/{id}", new { id, clientId, clientSecret = secret, note = "Copy the secret now: it is not stored and cannot be shown again." });
    }

    static async Task<IResult> Update(Guid id, ApiClientInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (N(i.Name) is not { } name || name.Length > 60) return Bad("A name is required (up to 60 characters).");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        short userId;
        await using (var f = new NpgsqlCommand("SELECT user_id FROM api_client WHERE id = $1", cn, tx))
        { f.Parameters.AddWithValue(id); if (await f.ExecuteScalarAsync(ct) is not short u) return Results.NotFound(); userId = u; }
        if (await CheckGroups(cn, tx, i.GroupIds, ct) is { } bad) return Bad(bad);
        await SetGroups(cn, tx, userId, i.GroupIds!, ct);
        await using (var c = new NpgsqlCommand("UPDATE api_client SET name = $2, note = $3, expires_at = $4 WHERE id = $1", cn, tx))
        {
            c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(name);
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)N(i.Note) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)i.ExpiresAt?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var a = new NpgsqlCommand("UPDATE app_user SET name = $2, active = $3 WHERE id = $1", cn, tx))
        { a.Parameters.AddWithValue(userId); a.Parameters.AddWithValue(name); a.Parameters.AddWithValue(i.Active); await a.ExecuteNonQueryAsync(ct); }
        if (!i.Active) await using (var t = new NpgsqlCommand("DELETE FROM api_token WHERE client_id = $1", cn, tx)) { t.Parameters.AddWithValue(id); await t.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>New secret; the old one and every token issued with it stop working at once.</summary>
    static async Task<IResult> Rotate(Guid id, NpgsqlDataSource db, CancellationToken ct)
    {
        var secret = NewSecret();
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("UPDATE api_client SET secret_hash = $2, secret_rotated_at = now() WHERE id = $1", cn, tx))
        { c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(Tokens.Hash(secret)); if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
        await using (var t = new NpgsqlCommand("DELETE FROM api_token WHERE client_id = $1", cn, tx)) { t.Parameters.AddWithValue(id); await t.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return Results.Ok(new { clientSecret = secret, note = "Copy the secret now: it is not stored and cannot be shown again." });
    }

    /// <summary>Removes the client and its tokens. The service account stays (inactive, no groups) because history rows may point at it.</summary>
    static async Task<IResult> Delete(Guid id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        short userId;
        await using (var f = new NpgsqlCommand("DELETE FROM api_client WHERE id = $1 RETURNING user_id", cn, tx))
        { f.Parameters.AddWithValue(id); if (await f.ExecuteScalarAsync(ct) is not short u) return Results.NotFound(); userId = u; }
        await using (var d = new NpgsqlCommand("DELETE FROM user_group WHERE user_id = $1", cn, tx)) { d.Parameters.AddWithValue(userId); await d.ExecuteNonQueryAsync(ct); }
        await using (var a = new NpgsqlCommand("UPDATE app_user SET active = false WHERE id = $1", cn, tx)) { a.Parameters.AddWithValue(userId); await a.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
