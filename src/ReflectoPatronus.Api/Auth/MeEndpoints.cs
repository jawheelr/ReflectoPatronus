using System.Security.Claims;
using Npgsql;

namespace ReflectoPatronus.Api.Auth;

public record MeResponse(int Id, string Code, string? Name, string TimeZone, string? UserTimeZone, string CompanyTimeZone, bool IsAdmin, IReadOnlyList<string> Permissions, IReadOnlyList<string> Groups);
public record UpdateSettingsRequest(string? TimeZone);

public static class MeEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/me", Get);
        api.MapPut("/me/settings", UpdateSettings);
        api.MapGet("/timezones", ListTimeZones);
    }

    static async Task<IResult> Get(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
        => Results.Ok(await Load(db, user.UserId(), ct));

    /// <summary>Per-user display time zone. null clears it (falls back to the company default). Stored instants are never changed.</summary>
    static async Task<IResult> UpdateSettings(UpdateSettingsRequest req, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (req.TimeZone is not null)
        {
            await using var chk = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = $1)");
            chk.Parameters.AddWithValue(req.TimeZone);
            if (!(bool)(await chk.ExecuteScalarAsync(ct))!)
                return Results.BadRequest(new { error = "Unknown time zone. Use an IANA name such as America/Chicago (see GET /api/v1/timezones)." });
        }
        await using var cmd = db.CreateCommand("UPDATE app_user SET time_zone = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(user.UserId());
        cmd.Parameters.AddWithValue((object?)req.TimeZone ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return Results.Ok(await Load(db, user.UserId(), ct));
    }

    /// <summary>Canonical IANA names for a picker (excludes legacy aliases and POSIX/Etc entries).</summary>
    static async Task<IResult> ListTimeZones(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT name FROM pg_timezone_names
            WHERE name ~ '^(Africa|America|Antarctica|Asia|Atlantic|Australia|Europe|Indian|Pacific)/' ORDER BY name
            """);
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        return Results.Ok(names);
    }

    static async Task<MeResponse> Load(NpgsqlDataSource db, short id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT u.code, u.name, u.time_zone, u.is_admin, coalesce((SELECT time_zone FROM company ORDER BY id LIMIT 1), 'America/Chicago')
            FROM app_user u WHERE u.id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var userTz = r.IsDBNull(2) ? null : r.GetString(2);
        var company = r.GetString(4);
        var code = r.GetString(0); var name = r.IsDBNull(1) ? null : r.GetString(1);
        await r.CloseAsync();
        var perms = await Auth.Permissions.Load(db, id, ct);
        var groups = new List<string>();
        await using (var g = db.CreateCommand("SELECT g.name FROM user_group ug JOIN app_group g ON g.id = ug.group_id WHERE ug.user_id = $1 ORDER BY g.name")) { g.Parameters.AddWithValue(id); await using var gr = await g.ExecuteReaderAsync(ct); while (await gr.ReadAsync(ct)) groups.Add(gr.GetString(0)); }
        return new MeResponse(id, code, name, userTz ?? company, userTz, company, perms.Contains(Auth.Permissions.All), perms.Order().ToList(), groups);
    }
}
