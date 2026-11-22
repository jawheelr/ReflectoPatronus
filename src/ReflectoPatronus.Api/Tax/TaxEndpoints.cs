using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Tax;

public record TaxCode(string Code, string? Description, decimal Percent, bool TaxesLabor, bool NonCalc);
public record TaxGroupCode(int Position, string Code, string? Description, decimal Percent);
public record TaxGroup(string Code, string? Description, IReadOnlyList<TaxGroupCode> Codes);
public record TaxGroupCodeInput(int Position, string? Code);
public record TaxGroupInput(string? Code, string? Description, IReadOnlyList<TaxGroupCodeInput>? Codes);

public static class TaxEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/tax-codes", ListCodes);
        api.MapGet("/tax-groups", ListGroups);
        api.MapPost("/tax-groups", CreateGroup).RequirePermission("maintenance.write");
        api.MapPut("/tax-groups/{code}", UpdateGroup).RequirePermission("maintenance.write");
        api.MapDelete("/tax-groups/{code}", DeleteGroup).RequirePermission("maintenance.write");
    }

    static async Task<IResult> ListCodes(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT code, description, percent, taxes_labor, non_calc FROM tax_code ORDER BY code");
        var items = new List<TaxCode>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new TaxCode(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetDecimal(2), r.GetBoolean(3), r.GetBoolean(4)));
        return Results.Ok(items);
    }

    static async Task<IResult> ListGroups(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT g.code, g.description, gc.position, c.code, c.description, c.percent
            FROM tax_group g
            LEFT JOIN tax_group_code gc ON gc.tax_group = g.code
            LEFT JOIN tax_code c ON c.code = gc.tax_code
            ORDER BY g.code, gc.position
            """);
        var groups = new List<TaxGroup>(); string? cur = null; string? desc = null; var codes = new List<TaxGroupCode>();
        void Flush() { if (cur is not null) groups.Add(new TaxGroup(cur, desc, codes.ToList())); codes.Clear(); }
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var code = r.GetString(0);
            if (code != cur) { Flush(); cur = code; desc = r.IsDBNull(1) ? null : r.GetString(1); }
            if (!r.IsDBNull(2))
                codes.Add(new TaxGroupCode(r.GetInt16(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5)));
        }
        Flush();
        return Results.Ok(groups);
    }

    // ---- tax group editing: a group is a code + description + up to five tax codes in positions 1-5 ----

    static string? Clean(TaxGroupInput g, out List<(short Pos, string Code)> codes)
    {
        codes = [];
        foreach (var c in g.Codes ?? [])
        {
            var code = string.IsNullOrWhiteSpace(c.Code) ? null : c.Code.Trim();
            if (code is null) continue;
            if (c.Position is < 1 or > 5) return "Tax code positions are 1-5.";
            if (codes.Any(x => x.Pos == c.Position)) return "Duplicate position.";
            if (codes.Any(x => x.Code == code)) return $"Tax code {code} is used twice.";
            codes.Add(((short)c.Position, code));
        }
        return null;
    }

    static async Task WriteCodes(NpgsqlConnection cn, NpgsqlTransaction tx, string group, List<(short Pos, string Code)> codes, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM tax_group_code WHERE tax_group = $1", cn, tx))
        { del.Parameters.AddWithValue(group); await del.ExecuteNonQueryAsync(ct); }
        foreach (var (pos, code) in codes)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO tax_group_code (tax_group, position, tax_code) VALUES ($1, $2, $3)", cn, tx);
            ins.Parameters.AddWithValue(group); ins.Parameters.AddWithValue(pos); ins.Parameters.AddWithValue(code);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    static IResult PgProblem(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => Results.Json(new { error = "That tax group already exists." }, statusCode: 409),
        PostgresErrorCodes.ForeignKeyViolation => Results.BadRequest(new { error = "Unknown tax code." }),
        _ => throw e,
    };

    static async Task<IResult> CreateGroup(TaxGroupInput g, NpgsqlDataSource db, CancellationToken ct)
    {
        var code = g.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20) return Results.BadRequest(new { error = "Code is required (20 characters at most)." });
        if (Clean(g, out var codes) is { } err) return Results.BadRequest(new { error = err });
        await using var cn = await db.OpenConnectionAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            await using var ins = new NpgsqlCommand("INSERT INTO tax_group (code, description) VALUES ($1, $2)", cn, tx);
            ins.Parameters.AddWithValue(code); ins.Parameters.AddWithValue((object?)(string.IsNullOrWhiteSpace(g.Description) ? null : g.Description.Trim()) ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
            await WriteCodes(cn, tx, code, codes, ct); await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/tax-groups/{code}", new { code });
        }
        catch (PostgresException e) { return PgProblem(e); }
    }

    static async Task<IResult> UpdateGroup(string code, TaxGroupInput g, NpgsqlDataSource db, CancellationToken ct)
    {
        if (Clean(g, out var codes) is { } err) return Results.BadRequest(new { error = err });
        await using var cn = await db.OpenConnectionAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            await using var up = new NpgsqlCommand("UPDATE tax_group SET description = $2 WHERE code = $1", cn, tx);
            up.Parameters.AddWithValue(code); up.Parameters.AddWithValue((object?)(string.IsNullOrWhiteSpace(g.Description) ? null : g.Description.Trim()) ?? DBNull.Value);
            if (await up.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await WriteCodes(cn, tx, code, codes, ct); await tx.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (PostgresException e) { return PgProblem(e); }
    }

    static async Task<IResult> DeleteGroup(string code, NpgsqlDataSource db, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand("DELETE FROM tax_group WHERE code = $1");
            cmd.Parameters.AddWithValue(code);
            return await cmd.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Json(new { error = "This tax group is used by customers, documents or the company and cannot be deleted." }, statusCode: 409);
        }
    }
}
