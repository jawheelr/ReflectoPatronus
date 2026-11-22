using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Maintenance;

public enum ColType { Text, Bool, Decimal }

/// <summary>One column of a maintenance table. Api = JSON name, Db = column name (both come from this registry, never from the request).</summary>
public record Col(string Api, string Db, string Label, ColType Type = ColType.Text, bool Required = false, int Max = 60,
    bool ReadOnly = false, decimal Min = 0, decimal MaxValue = 0);

/// <summary>A small reference table edited generically. KeyIdentity: the key is a generated integer (not typed by the user).</summary>
public record TableDef(string Key, string Title, string Table, Col KeyCol, bool KeyIdentity, Col[] Cols, string OrderBy, string? Note = null,
    string? ProtectedDb = null);

/// <summary>
/// Generic list / create / update / delete for the small code tables (tax codes, categories, salesmen/installers, statuses).
/// Reading is open to every signed-in user; changing them is administrator-only. Table and column names only ever come from
/// the registry below, values are always parameters.
/// </summary>
public static class MaintenanceEndpoints
{
    static readonly Col Code = new("code", "code", "Code", Required: true, Max: 20);
    static readonly Col Desc = new("description", "description", "Description", Max: 80);

    public static readonly TableDef[] Tables =
    [
        new("tax-codes", "Tax Codes Entry", "tax_code", Code, false, [Desc,
            new("percent", "percent", "Percent", ColType.Decimal, true, Min: 0, MaxValue: 100),
            new("taxesLabor", "taxes_labor", "Labor taxable", ColType.Bool), new("nonCalc", "non_calc", "Non-calculative", ColType.Bool)], "code"),
        new("customer-categories", "Customer Categories", "customer_category", Code, false, [Desc], "code"),
        new("price-categories", "Price Categories", "price_category", Code, false, [Desc], "code",
            Note: "Markup percentages for price categories are set under Utilities > Supplier Pricing."),
        new("pay-categories", "Payment Categories", "pay_category", Code, false, [Desc, new("undeposited", "undeposited", "Undeposited funds", ColType.Bool)], "code"),
        new("staff", "Salesman/Installer", "staff", new Col("id", "id", "Id", ReadOnly: true), true, [
            new("code", "code", "Code", Required: true, Max: 20), new("name", "name", "Name", Max: 80), new("phone", "phone", "Phone", Max: 30),
            new("rate", "rate", "Rate", ColType.Decimal, Min: 0, MaxValue: 99_999_999),
            new("isSalesman", "is_salesman", "Salesman", ColType.Bool), new("isInstaller", "is_installer", "Installer", ColType.Bool),
            new("isRegion", "is_region", "Region", ColType.Bool), new("deleted", "deleted", "Inactive", ColType.Bool),
            new("hours", "hours", "Hours", Max: 40), new("lunch", "lunch", "Lunch", Max: 40)], "lower(code)",
            Note: "A person with documents cannot be deleted: tick Inactive instead."),
        new("statuses", "Status Table", "document_status", new Col("id", "id", "Id", ReadOnly: true), true, [
            new("name", "name", "Name", Required: true, Max: 40), new("obsolete", "obsolete", "Obsolete", ColType.Bool),
            new("isSystem", "is_system", "System status", ColType.Bool, ReadOnly: true)], "id",
            Note: "System statuses cannot be deleted.", ProtectedDb: "is_system"),
    ];

    static TableDef? Find(string key) => Tables.FirstOrDefault(t => t.Key == key);
    static string Q(string ident) => "\"" + ident + "\"";   // identifiers come from the registry; quoted anyway

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/maintenance");
        g.MapGet("", () => Results.Ok(Tables.Select(t => new { t.Key, t.Title, t.Note, KeyColumn = t.KeyCol, t.KeyIdentity, t.Cols, Protected = t.ProtectedDb is not null })));
        g.MapGet("/{table}", List);
        g.MapPost("/{table}", Create).RequirePermission("maintenance.write");
        g.MapPut("/{table}/{key}", Update).RequirePermission("maintenance.write");
        g.MapDelete("/{table}/{key}", Delete).RequirePermission("maintenance.write");
    }

    static IEnumerable<Col> AllCols(TableDef t) => t.KeyIdentity ? t.Cols : t.Cols.Prepend(t.KeyCol);

    static async Task<IResult> List(string table, NpgsqlDataSource db, CancellationToken ct)
    {
        var t = Find(table); if (t is null) return Results.NotFound();
        var cols = AllCols(t).Prepend(t.KeyCol).DistinctBy(c => c.Db).ToArray();
        var prot = t.ProtectedDb is null ? "" : $", {Q(t.ProtectedDb)}";
        await using var cmd = db.CreateCommand($"SELECT {string.Join(", ", cols.Select(c => Q(c.Db)))}{(t.ProtectedDb is null || cols.Any(c => c.Db == t.ProtectedDb) ? "" : prot)} FROM {Q(t.Table)} ORDER BY {t.OrderBy}");
        var rows = new List<Dictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < cols.Length; i++) row[cols[i].Api] = r.IsDBNull(i) ? null : cols[i].Type == ColType.Decimal ? r.GetDecimal(i) : r.GetValue(i);
            rows.Add(row);
        }
        return Results.Ok(rows);
    }

    /// <summary>Reads and validates the editable columns from the JSON body. Returns (db column, parameter) pairs or an error.</summary>
    static (List<(Col Col, NpgsqlParameter P)>? Values, string? Error) Parse(TableDef t, JsonElement body, bool creating)
    {
        var list = new List<(Col, NpgsqlParameter)>();
        foreach (var c in AllCols(t).Where(c => !c.ReadOnly))
        {
            if (!creating && ReferenceEquals(c, t.KeyCol)) continue;   // a code never changes after creation: other tables reference it
            body.TryGetProperty(c.Api, out var v);
            var present = v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
            switch (c.Type)
            {
                case ColType.Bool:
                    list.Add((c, new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = present && v.ValueKind == JsonValueKind.True }));
                    break;
                case ColType.Decimal:
                    decimal? d = null;
                    if (present)
                    {
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) d = n;
                        else if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n2)) d = n2;
                        else if (!(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()))) return (null, $"{c.Label} must be a number.");
                    }
                    if (d is null && c.Required) return (null, $"{c.Label} is required.");
                    if (d is { } x && (x < c.Min || (c.MaxValue > 0 && x > c.MaxValue))) return (null, $"{c.Label} must be between {c.Min} and {c.MaxValue}.");
                    list.Add((c, new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = (object?)d ?? DBNull.Value }));
                    break;
                default:
                    var s = present && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
                    if (string.IsNullOrEmpty(s)) s = null;
                    if (s is null && c.Required) return (null, $"{c.Label} is required.");
                    if (s is not null && s.Length > c.Max) return (null, $"{c.Label} is too long (max {c.Max}).");
                    list.Add((c, new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)s ?? DBNull.Value }));
                    break;
            }
        }
        return (list, null);
    }

    static IResult Problem(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => Results.Json(new { error = "That code already exists." }, statusCode: 409),
        PostgresErrorCodes.ForeignKeyViolation => Results.Json(new { error = "It is in use by other records, so it cannot be removed." }, statusCode: 409),
        PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.StringDataRightTruncation
            or PostgresErrorCodes.NumericValueOutOfRange => Results.BadRequest(new { error = "A value is not acceptable." }),
        _ => throw e,
    };

    static async Task<IResult> Create(string table, JsonElement body, NpgsqlDataSource db, CancellationToken ct)
    {
        var t = Find(table); if (t is null) return Results.NotFound();
        var (vals, error) = Parse(t, body, creating: true);
        if (vals is null) return Results.BadRequest(new { error });
        await using var cmd = db.CreateCommand($"INSERT INTO {Q(t.Table)} ({string.Join(", ", vals.Select(v => Q(v.Col.Db)))}) VALUES ({string.Join(", ", vals.Select((_, i) => "$" + (i + 1)))}) RETURNING {Q(t.KeyCol.Db)}");
        foreach (var v in vals) cmd.Parameters.Add(v.P);
        try { return Results.Created($"/api/v1/maintenance/{t.Key}", new { key = Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) }); }
        catch (PostgresException e) { return Problem(e); }
    }

    static NpgsqlParameter? KeyParam(TableDef t, string key)
    {
        if (!t.KeyIdentity) return new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key };
        return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = n } : null;
    }

    static async Task<IResult> Update(string table, string key, JsonElement body, NpgsqlDataSource db, CancellationToken ct)
    {
        var t = Find(table); if (t is null) return Results.NotFound();
        var kp = KeyParam(t, key); if (kp is null) return Results.NotFound();
        var (vals, error) = Parse(t, body, creating: false);
        if (vals is null) return Results.BadRequest(new { error });
        if (vals.Count == 0) return Results.NoContent();
        await using var cmd = db.CreateCommand($"UPDATE {Q(t.Table)} SET {string.Join(", ", vals.Select((v, i) => $"{Q(v.Col.Db)} = ${i + 1}"))} WHERE {Q(t.KeyCol.Db)} = ${vals.Count + 1}");
        foreach (var v in vals) cmd.Parameters.Add(v.P);
        cmd.Parameters.Add(kp);
        try { return await cmd.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent(); }
        catch (PostgresException e) { return Problem(e); }
    }

    static async Task<IResult> Delete(string table, string key, NpgsqlDataSource db, CancellationToken ct)
    {
        var t = Find(table); if (t is null) return Results.NotFound();
        var kp = KeyParam(t, key); if (kp is null) return Results.NotFound();
        var guard = t.ProtectedDb is null ? "" : $" AND NOT {Q(t.ProtectedDb)}";
        await using var cmd = db.CreateCommand($"DELETE FROM {Q(t.Table)} WHERE {Q(t.KeyCol.Db)} = $1{guard}");
        cmd.Parameters.Add(kp);
        try
        {
            if (await cmd.ExecuteNonQueryAsync(ct) > 0) return Results.NoContent();
            return t.ProtectedDb is not null ? Results.Json(new { error = "System entries cannot be deleted (or it no longer exists)." }, statusCode: 409) : Results.NotFound();
        }
        catch (PostgresException e) { return Problem(e); }
    }
}
