using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Auth;
using ReflectoPatronus.Api.Company;
using ReflectoPatronus.Api.Customers;

namespace ReflectoPatronus.Api.Admin;

public record UserRow(int Id, string Code, string? Name, bool Active, bool IsAdmin, bool HasPassword, DateTime? LockedUntil, int Sessions, IReadOnlyList<int> GroupIds);
public record UserInput(string? Code, string? Name, string? Password, IReadOnlyList<int>? GroupIds, bool Active);
public record GroupRow(int Id, string Code, string Name, string? Description, bool IsSystem, bool Locked, IReadOnlyList<string> Permissions, int Members);
public record GroupInput(string? Name, string? Description, IReadOnlyList<string>? Permissions);
public record PasswordChange(string? Current, string? New);
public record PasswordReset(string? Password);
public record CleanupRequest(DateOnly? Before, bool DryRun);

/// <summary>Company details, user accounts, passwords and the Utilities clean-up tools. All of it except changing your own password is administrator-only.</summary>
public static class AdminEndpoints
{
    static readonly PasswordHasher<object> Hasher = new();
    const int MinPassword = 12;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPut("/company", PutCompany).RequirePermission("company.write");
        api.MapPost("/me/password", ChangeOwn);
        var u = api.MapGroup("/users").RequirePermission("users.manage");
        u.MapGet("", Users); u.MapPost("", CreateUser); u.MapPut("/{id:int}", UpdateUser); u.MapPost("/{id:int}/password", ResetPassword);
        var gr = api.MapGroup("/groups").RequirePermission("users.manage");
        gr.MapGet("", Groups); gr.MapPost("", CreateGroup); gr.MapPut("/{id:int}", UpdateGroup); gr.MapDelete("/{id:int}", DeleteGroup);
        api.MapGet("/permissions", () => Results.Ok(Permissions.Catalog)).RequirePermission("users.manage");
        var c = api.MapGroup("/utilities").RequirePermission("utilities.cleanup");
        c.MapPost("/delete-quotes", DeleteQuotes); c.MapPost("/delete-inactive-customers", DeleteCustomers);
    }

    static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static NpgsqlParameter P(object? v, NpgsqlDbType t) => new() { NpgsqlDbType = t, Value = v ?? DBNull.Value };
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static IResult Conflict(string m) => Results.Json(new { error = m }, statusCode: 409);
    static string? CheckPassword(string? p) => p is null || p.Length < MinPassword ? $"The password must be at least {MinPassword} characters." : p.Length > 200 ? "The password is too long." : null;

    static async Task<IResult> PutCompany(CompanyInfo i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (N(i.Name) is null) return Bad("The company name is required.");
        if (N(i.State) is { Length: not 2 }) return Bad("State must be two letters.");
        if (i.TimeZone is not null && !await ValidZone(db, i.TimeZone, ct)) return Bad("Unknown time zone.");
        await using var c = db.CreateCommand("""
            UPDATE company SET name = $1, address1 = $2, address2 = $3, city = $4, state = $5, zip = $6, phone = $7, fax = $8, email = $9,
                invoice_note = $10, disclaimer = $11, use_disclaimer = $12, time_zone = coalesce($13, time_zone)
            WHERE id = (SELECT id FROM company ORDER BY id LIMIT 1)
            """);
        c.Parameters.AddWithValue(i.Name.Trim());
        foreach (var v in new[] { i.Address1, i.Address2, i.City, i.State?.ToUpperInvariant(), i.Zip, i.Phone, i.Fax, i.Email, i.InvoiceNote, i.Disclaimer }) c.Parameters.Add(P(N(v), NpgsqlDbType.Text));
        c.Parameters.AddWithValue(i.UseDisclaimer); c.Parameters.Add(P(i.TimeZone, NpgsqlDbType.Text));
        await c.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }
    static async Task<bool> ValidZone(NpgsqlDataSource db, string z, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT 1 FROM pg_timezone_names WHERE name = $1"); c.Parameters.AddWithValue(z);
        return await c.ExecuteScalarAsync(ct) is not null;
    }

    static async Task<IResult> ChangeOwn(PasswordChange req, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Current)) return Bad("Enter your current password.");
        if (CheckPassword(req.New) is { } e) return Bad(e);
        if (req.New == req.Current) return Bad("The new password must be different.");
        string? hash;
        await using (var c = db.CreateCommand("SELECT password_hash FROM app_user WHERE id = $1")) { c.Parameters.AddWithValue(user.UserId()); hash = await c.ExecuteScalarAsync(ct) as string; }
        if (hash is null || Hasher.VerifyHashedPassword(new object(), hash, req.Current) == PasswordVerificationResult.Failed) return Results.Json(new { error = "Your current password is not right." }, statusCode: 403);
        await SetPassword(db, user.UserId(), req.New!, user.SessionId(), ct);
        return Results.NoContent();
    }

    /// <summary>Sets a password and signs the user out everywhere else (a changed password should end other sessions).</summary>
    static async Task SetPassword(NpgsqlDataSource db, int userId, string password, Guid? keepSession, CancellationToken ct)
    {
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("UPDATE app_user SET password_hash = $2, failed_logins = 0, locked_until = NULL WHERE id = $1", cn, tx))
        { c.Parameters.AddWithValue(userId); c.Parameters.AddWithValue(Hasher.HashPassword(new object(), password)); await c.ExecuteNonQueryAsync(ct); }
        await using (var c = new NpgsqlCommand("UPDATE device_session SET revoked_at = now() WHERE user_id = $1 AND revoked_at IS NULL AND ($2::uuid IS NULL OR id <> $2)", cn, tx))
        { c.Parameters.AddWithValue(userId); c.Parameters.Add(P(keepSession, NpgsqlDbType.Uuid)); await c.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    static async Task<IResult> Users(NpgsqlDataSource db, CancellationToken ct)
    {
        var groups = new Dictionary<int, List<int>>();
        await using (var g = db.CreateCommand("SELECT user_id, group_id FROM user_group ORDER BY group_id"))
        await using (var gr = await g.ExecuteReaderAsync(ct))
            while (await gr.ReadAsync(ct)) (groups.TryGetValue(gr.GetInt16(0), out var l) ? l : groups[gr.GetInt16(0)] = []).Add(gr.GetInt16(1));
        await using var c = db.CreateCommand("""
            SELECT u.id, u.code, u.name, u.active, u.is_admin, u.password_hash IS NOT NULL, u.locked_until,
                   (SELECT count(*) FROM device_session s WHERE s.user_id = u.id AND s.revoked_at IS NULL AND s.refresh_expires_at > now())
            FROM app_user u WHERE NOT u.is_service ORDER BY u.active DESC, (u.password_hash IS NOT NULL) DESC, lower(u.code)
            """);
        var l2 = new List<UserRow>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l2.Add(new(r.GetInt16(0), r.GetString(1), CustomerEndpoints.Str(r, 2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetDateTime(6), (int)r.GetInt64(7), groups.GetValueOrDefault(r.GetInt16(0)) ?? []));
        return Results.Ok(l2);
    }

    /// <summary>Replaces a user's groups, keeps the derived is_admin flag in step, and refuses to leave the system without an administrator.</summary>
    static async Task<IResult?> SetGroups(NpgsqlConnection cn, NpgsqlTransaction tx, int userId, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        await using (var c = new NpgsqlCommand("SELECT count(*) FROM app_group WHERE id = ANY($1)", cn, tx))
        { c.Parameters.AddWithValue(distinct.Select(x => (short)x).ToArray()); if (Convert.ToInt32(await c.ExecuteScalarAsync(ct)) != distinct.Length) return Bad("Unknown group."); }
        await using (var c = new NpgsqlCommand("DELETE FROM user_group WHERE user_id = $1", cn, tx)) { c.Parameters.AddWithValue(userId); await c.ExecuteNonQueryAsync(ct); }
        foreach (var gid in distinct)
        {
            await using var c = new NpgsqlCommand("INSERT INTO user_group (user_id, group_id) VALUES ($1, $2)", cn, tx);
            c.Parameters.AddWithValue(userId); c.Parameters.AddWithValue((short)gid); await c.ExecuteNonQueryAsync(ct);
        }
        return null;
    }
    static async Task<IResult?> SyncAdmin(NpgsqlConnection cn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using (var c = new NpgsqlCommand("UPDATE app_user u SET is_admin = EXISTS (SELECT 1 FROM user_group ug JOIN app_group g ON g.id = ug.group_id WHERE ug.user_id = u.id AND g.code = 'administrators') WHERE is_admin IS DISTINCT FROM EXISTS (SELECT 1 FROM user_group ug JOIN app_group g ON g.id = ug.group_id WHERE ug.user_id = u.id AND g.code = 'administrators')", cn, tx))
            await c.ExecuteNonQueryAsync(ct);
        await using var k = new NpgsqlCommand("SELECT count(*) FROM app_user WHERE is_admin AND active AND password_hash IS NOT NULL", cn, tx);
        return Convert.ToInt32(await k.ExecuteScalarAsync(ct)) == 0 ? Conflict("There must always be at least one active administrator who can sign in.") : null;
    }

    static async Task<IResult> CreateUser(UserInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        var code = N(i.Code);
        if (code is null || code.Length > 20 || code.Any(char.IsWhiteSpace)) return Bad("The sign-in name is required: up to 20 characters, no spaces.");
        if (CheckPassword(i.Password) is { } e) return Bad(e);
        if (i.GroupIds is null || i.GroupIds.Count == 0) return Bad("Choose at least one group: it decides what this person can do.");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            int id;
            await using (var c = new NpgsqlCommand("INSERT INTO app_user (code, name, active, password_hash) VALUES ($1, $2, true, $3) RETURNING id", cn, tx))
            { c.Parameters.AddWithValue(code); c.Parameters.Add(P(N(i.Name) ?? code, NpgsqlDbType.Text)); c.Parameters.AddWithValue(Hasher.HashPassword(new object(), i.Password!)); id = Convert.ToInt32(await c.ExecuteScalarAsync(ct)); }
            if (await SetGroups(cn, tx, id, i.GroupIds, ct) is { } bad) return bad;
            if (await SyncAdmin(cn, tx, ct) is { } err) return err;
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/users/{id}", new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return Conflict("That sign-in name is already used."); }
    }

    static async Task<IResult> UpdateUser(int id, UserInput i, ClaimsPrincipal me, NpgsqlDataSource db, CancellationToken ct)
    {
        if (i.GroupIds is null) return Bad("groupIds is required.");
        if (id == me.UserId() && !i.Active) return Conflict("You cannot deactivate yourself.");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("UPDATE app_user SET name = $2, active = $3 WHERE id = $1 AND NOT is_service", cn, tx))
        { c.Parameters.AddWithValue(id); c.Parameters.Add(P(N(i.Name), NpgsqlDbType.Text)); c.Parameters.AddWithValue(i.Active); if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
        if (id == me.UserId())   // you may not take your own administrator rights away
        {
            await using var k = new NpgsqlCommand("SELECT id FROM app_group WHERE code = 'administrators'", cn, tx);
            var adminId = Convert.ToInt32(await k.ExecuteScalarAsync(ct));
            await using var had = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM user_group WHERE user_id = $1 AND group_id = $2)", cn, tx);
            had.Parameters.AddWithValue(id); had.Parameters.AddWithValue((short)adminId);
            if ((bool)(await had.ExecuteScalarAsync(ct))! && !i.GroupIds.Contains(adminId)) return Conflict("You cannot remove your own Administrators membership.");
        }
        if (await SetGroups(cn, tx, id, i.GroupIds, ct) is { } bad) return bad;
        if (await SyncAdmin(cn, tx, ct) is { } err) return err;
        if (!i.Active)
            await using (var c = new NpgsqlCommand("UPDATE device_session SET revoked_at = now() WHERE user_id = $1 AND revoked_at IS NULL", cn, tx)) { c.Parameters.AddWithValue(id); await c.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    // ---- groups ---------------------------------------------------------------------------------------------------

    static async Task<IResult> Groups(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT g.id, g.code, g.name, g.description, g.is_system, coalesce((SELECT array_agg(permission ORDER BY permission) FROM group_permission WHERE group_id = g.id), '{}'),
                   (SELECT count(*) FROM user_group WHERE group_id = g.id) FROM app_group g ORDER BY g.is_system DESC, g.id
            """);
        var l = new List<GroupRow>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new(r.GetInt16(0), r.GetString(1), r.GetString(2), CustomerEndpoints.Str(r, 3), r.GetBoolean(4), r.GetString(1) == "administrators", r.GetFieldValue<string[]>(5), (int)r.GetInt64(6)));
        return Results.Ok(l);
    }

    static string? CheckGroup(GroupInput i)
    {
        if (N(i.Name) is not { } n || n.Length > 40) return "Give the group a name (up to 40 characters).";
        if ((N(i.Description)?.Length ?? 0) > 200) return "The description is too long (max 200).";
        if ((i.Permissions ?? []).Any(p => !Permissions.Keys.Contains(p))) return "Unknown permission.";
        return null;
    }

    static async Task WritePerms(NpgsqlConnection cn, NpgsqlTransaction tx, int id, IEnumerable<string> perms, CancellationToken ct)
    {
        await using (var c = new NpgsqlCommand("DELETE FROM group_permission WHERE group_id = $1", cn, tx)) { c.Parameters.AddWithValue((short)id); await c.ExecuteNonQueryAsync(ct); }
        foreach (var p in perms.Distinct())
        {
            await using var c = new NpgsqlCommand("INSERT INTO group_permission (group_id, permission) VALUES ($1, $2)", cn, tx);
            c.Parameters.AddWithValue((short)id); c.Parameters.AddWithValue(p); await c.ExecuteNonQueryAsync(ct);
        }
    }

    static async Task<IResult> CreateGroup(GroupInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (CheckGroup(i) is { } err) return Bad(err);
        var code = new string(i.Name!.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (code.Length == 0) return Bad("Give the group a name using letters or numbers.");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            int id;
            await using (var c = new NpgsqlCommand("INSERT INTO app_group (code, name, description) VALUES ($1, $2, $3) RETURNING id", cn, tx))
            { c.Parameters.AddWithValue(code); c.Parameters.AddWithValue(i.Name!.Trim()); c.Parameters.Add(P(N(i.Description), NpgsqlDbType.Text)); id = Convert.ToInt32(await c.ExecuteScalarAsync(ct)); }
            await WritePerms(cn, tx, id, i.Permissions ?? [], ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/groups/{id}", new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return Conflict("A group with that name already exists."); }
    }

    static async Task<IResult> UpdateGroup(int id, GroupInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (CheckGroup(i) is { } err) return Bad(err);
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        string code; bool system;
        await using (var c = new NpgsqlCommand("SELECT code, is_system FROM app_group WHERE id = $1 FOR UPDATE", cn, tx))
        {
            c.Parameters.AddWithValue((short)id); await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound(); (code, system) = (r.GetString(0), r.GetBoolean(1));
        }
        if (code == "administrators") return Conflict("The Administrators group always has every permission and cannot be changed.");
        try
        {
            await using (var c = new NpgsqlCommand("UPDATE app_group SET name = $2, description = $3 WHERE id = $1", cn, tx))
            { c.Parameters.AddWithValue((short)id); c.Parameters.AddWithValue(i.Name!.Trim()); c.Parameters.Add(P(N(i.Description), NpgsqlDbType.Text)); await c.ExecuteNonQueryAsync(ct); }
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return Conflict("A group with that name already exists."); }
        await WritePerms(cn, tx, id, i.Permissions ?? [], ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> DeleteGroup(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT is_system, (SELECT count(*) FROM user_group WHERE group_id = $1) FROM app_group WHERE id = $1");
        c.Parameters.AddWithValue((short)id);
        bool system; long members;
        await using (var r = await c.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) return Results.NotFound(); (system, members) = (r.GetBoolean(0), r.GetInt64(1)); }
        if (system) return Conflict("Built-in groups cannot be deleted.");
        if (members > 0) return Conflict($"{members} user(s) are in this group. Move them to another group first.");
        await using var d = db.CreateCommand("DELETE FROM app_group WHERE id = $1"); d.Parameters.AddWithValue((short)id); await d.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> ResetPassword(int id, PasswordReset r, ClaimsPrincipal me, NpgsqlDataSource db, CancellationToken ct)
    {
        if (CheckPassword(r.Password) is { } e) return Bad(e);
        await using (var c = db.CreateCommand("SELECT 1 FROM app_user WHERE id = $1 AND NOT is_service")) { c.Parameters.AddWithValue(id); if (await c.ExecuteScalarAsync(ct) is null) return Results.NotFound(); }
        await SetPassword(db, id, r.Password!, id == me.UserId() ? me.SessionId() : null, ct);
        return Results.NoContent();
    }

    static async Task<IResult> DeleteQuotes(CleanupRequest r, NpgsqlDataSource db, CancellationToken ct)
    {
        if (r.Before is null) return Bad("Choose a date: quotes before it are deleted.");
        var sql = r.DryRun ? "SELECT count(*) FROM document WHERE doc_type = 'Q' AND doc_date < $1"
            : "WITH d AS (DELETE FROM document WHERE doc_type = 'Q' AND doc_date < $1 RETURNING 1) SELECT count(*) FROM d";
        await using var c = db.CreateCommand(sql); c.Parameters.Add(P(r.Before.Value.ToDateTime(TimeOnly.MinValue), NpgsqlDbType.Date));
        return Results.Ok(new { count = Convert.ToInt32(await c.ExecuteScalarAsync(ct)), deleted = !r.DryRun });
    }

    /// <summary>Customers with no documents, payments or ledger history who are not marked permanent (and, with a date, not used since).</summary>
    static async Task<IResult> DeleteCustomers(CleanupRequest r, NpgsqlDataSource db, CancellationToken ct)
    {
        const string where = """
            NOT permanent AND (last_transaction_on IS NULL OR $1::date IS NOT NULL AND last_transaction_on < $1::date)
            AND NOT EXISTS (SELECT 1 FROM document d WHERE d.customer_id = customer.id OR d.bill_to_customer_id = customer.id)
            AND NOT EXISTS (SELECT 1 FROM payment p WHERE p.customer_id = customer.id) AND NOT EXISTS (SELECT 1 FROM ar_entry a WHERE a.customer_id = customer.id)
            """;
        var sql = r.DryRun ? $"SELECT count(*) FROM customer WHERE {where}" : $"WITH d AS (DELETE FROM customer WHERE {where} RETURNING 1) SELECT count(*) FROM d";
        await using var c = db.CreateCommand(sql); c.Parameters.Add(P(r.Before?.ToDateTime(TimeOnly.MinValue), NpgsqlDbType.Date));
        return Results.Ok(new { count = Convert.ToInt32(await c.ExecuteScalarAsync(ct)), deleted = !r.DryRun });
    }
}
