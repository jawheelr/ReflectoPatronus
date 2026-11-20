using Npgsql;

namespace ReflectoPatronus.Api.Tax;

public record TaxCode(string Code, string? Description, decimal Percent, bool TaxesLabor, bool NonCalc);
public record TaxGroupCode(int Position, string Code, string? Description, decimal Percent);
public record TaxGroup(string Code, string? Description, IReadOnlyList<TaxGroupCode> Codes);

public static class TaxEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/tax-codes", ListCodes);
        api.MapGet("/tax-groups", ListGroups);
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
}
