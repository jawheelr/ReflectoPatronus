using System.Security.Claims;
using System.Text.Json;
using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Pricing;

public record SupplierInput(string Name, bool Active, string SourceType, string? FeedUrl, string? FeedAuthHeader, string FeedFormat, JsonElement ColumnMap, int? RefreshHours);
public record CostsInput(List<CostRowInput> Rows);
public record CostRowInput(string Sku, string? Description, decimal Cost);
public record AdjustInput(decimal Percent);
public record ImportInput(string Content);
public record LinkInput(int SupplierItemId, decimal Factor);
public record MarkupInput(decimal? MarkupPct);

/// <summary>Supplier cost feeds and reviewed price changes. Everything here is admin-only.</summary>
public static class PricingEndpoints
{
    static string[] SourceTypes = ["manual", "upload", "feed"];

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/pricing").RequirePermission("pricing.manage");

        g.MapGet("/suppliers", ListSuppliers);
        g.MapPost("/suppliers", (SupplierInput i, NpgsqlDataSource db, CancellationToken ct) => SaveSupplier(null, i, db, ct));
        g.MapPut("/suppliers/{id:int}", (int id, SupplierInput i, NpgsqlDataSource db, CancellationToken ct) => SaveSupplier(id, i, db, ct));
        g.MapDelete("/suppliers/{id:int}", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("DELETE FROM supplier WHERE id = $1"); c.Parameters.AddWithValue(id);
            return await c.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });
        g.MapGet("/suppliers/{id:int}/items", ListItems);
        g.MapPost("/suppliers/{id:int}/costs", Costs);
        g.MapPost("/suppliers/{id:int}/adjust", Adjust);
        g.MapPost("/suppliers/{id:int}/import", Import);
        g.MapPost("/suppliers/{id:int}/run", RunNow);
        g.MapGet("/links", ListLinks);
        g.MapGet("/thicknesses", SearchThicknesses);
        g.MapPut("/links/{thicknessId:int}", PutLink);
        g.MapDelete("/links/{thicknessId:int}", async (int thicknessId, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("DELETE FROM part_cost_source WHERE part_thickness_id = $1"); c.Parameters.AddWithValue(thicknessId);
            await c.ExecuteNonQueryAsync(ct); return Results.NoContent();
        });
        g.MapGet("/markups", Markups);
        g.MapPut("/markups/{code}", PutMarkup);
        g.MapPost("/recalculate", async (NpgsqlDataSource db, CancellationToken ct) => Results.Ok(await Propose(db, null, "markup", ct)));
        g.MapGet("/batches", Batches);
        g.MapGet("/batches/{id:int}", BatchDetail);
        g.MapPost("/batches/{id:int}/approve", (int id, ClaimsPrincipal u, NpgsqlDataSource db, CancellationToken ct) => Decide(id, true, u, db, ct));
        g.MapPost("/batches/{id:int}/reject", (int id, ClaimsPrincipal u, NpgsqlDataSource db, CancellationToken ct) => Decide(id, false, u, db, ct));
    }

    static IResult Bad(string m) => Results.BadRequest(new { error = m });

    static async Task<IResult> ListSuppliers(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT s.id, s.name, s.active, s.source_type, s.feed_url, s.feed_auth_header IS NOT NULL, s.feed_format, s.column_map::text, s.refresh_hours,
                   s.last_run_at, s.last_status, s.last_message,
                   (SELECT count(*) FROM supplier_item i WHERE i.supplier_id = s.id),
                   (SELECT count(*) FROM part_cost_source p JOIN supplier_item i ON i.id = p.supplier_item_id WHERE i.supplier_id = s.id)
            FROM supplier s ORDER BY lower(s.name)
            """);
        var list = new List<object>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new
            {
                id = r.GetInt32(0), name = r.GetString(1), active = r.GetBoolean(2), sourceType = r.GetString(3),
                feedUrl = r.IsDBNull(4) ? null : r.GetString(4), hasAuthHeader = r.GetBoolean(5), feedFormat = r.GetString(6),
                columnMap = JsonDocument.Parse(r.GetString(7)).RootElement.Clone(), refreshHours = r.IsDBNull(8) ? (int?)null : r.GetInt32(8),
                lastRunAt = r.IsDBNull(9) ? (DateTime?)null : r.GetDateTime(9), lastStatus = r.IsDBNull(10) ? null : r.GetString(10),
                lastMessage = r.IsDBNull(11) ? null : r.GetString(11), itemCount = r.GetInt64(12), linkedCount = r.GetInt64(13),
            });
        return Results.Ok(list);
    }

    static async Task<IResult> SaveSupplier(int? id, SupplierInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return Bad("Name is required.");
        if (!SourceTypes.Contains(i.SourceType)) return Bad("Source must be manual, upload or feed.");
        if (i.FeedFormat is not ("csv" or "json")) return Bad("Format must be csv or json.");
        if (i.SourceType == "feed" && (!Uri.TryCreate(i.FeedUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)) return Bad("A feed needs a valid https:// URL.");
        if (i.RefreshHours is < 1 or > 8760) return Bad("Refresh interval must be 1 to 8760 hours.");
        var map = i.ColumnMap.ValueKind == JsonValueKind.Object ? i.ColumnMap.GetRawText() : """{"sku":"sku","cost":"cost","description":"description"}""";
        // feed_auth_header is write-only: omitted/blank on update keeps the stored value.
        var sql = id is null
            ? "INSERT INTO supplier (name, active, source_type, feed_url, feed_auth_header, feed_format, column_map, refresh_hours) VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8) RETURNING id"
            : "UPDATE supplier SET name=$1, active=$2, source_type=$3, feed_url=$4, feed_auth_header=coalesce($5, feed_auth_header), feed_format=$6, column_map=$7::jsonb, refresh_hours=$8 WHERE id=$9 RETURNING id";
        await using var c = db.CreateCommand(sql);
        c.Parameters.AddWithValue(i.Name.Trim()); c.Parameters.AddWithValue(i.Active); c.Parameters.AddWithValue(i.SourceType);
        c.Parameters.AddWithValue(i.SourceType == "feed" ? (object?)i.FeedUrl?.Trim() ?? DBNull.Value : DBNull.Value);
        c.Parameters.AddWithValue(string.IsNullOrWhiteSpace(i.FeedAuthHeader) ? DBNull.Value : i.FeedAuthHeader);
        c.Parameters.AddWithValue(i.FeedFormat); c.Parameters.AddWithValue(map);
        c.Parameters.AddWithValue(i.SourceType == "feed" && i.RefreshHours is not null ? i.RefreshHours : DBNull.Value);
        if (id is not null) c.Parameters.AddWithValue(id.Value);
        try
        {
            var res = await c.ExecuteScalarAsync(ct);
            return res is null ? Results.NotFound() : Results.Ok(new { id = (int)res });
        }
        catch (PostgresException e) when (e.SqlState == "23505") { return Bad("A supplier with that name already exists."); }
    }

    static async Task<IResult> ListItems(int id, string? q, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT i.id, i.sku, i.description, i.unit_cost, i.cost_updated_at, (SELECT count(*) FROM part_cost_source p WHERE p.supplier_item_id = i.id)
            FROM supplier_item i WHERE i.supplier_id = $1 AND ($2::text IS NULL OR i.sku ILIKE $2 OR i.description ILIKE $2)
            ORDER BY lower(i.sku) LIMIT 500
            """);
        c.Parameters.AddWithValue(id);
        c.Parameters.AddWithValue(string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + Customers.CustomerEndpoints.EscapeLike(q.Trim()) + "%");
        var list = new List<object>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { id = r.GetInt32(0), sku = r.GetString(1), description = r.IsDBNull(2) ? null : r.GetString(2), unitCost = r.GetDecimal(3), costUpdatedAt = r.GetDateTime(4), links = r.GetInt64(5) });
        return Results.Ok(list);
    }

    static async Task<IResult> Costs(int id, CostsInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (i.Rows is not { Count: > 0 }) return Bad("Provide at least one row.");
        if (i.Rows.Any(r => string.IsNullOrWhiteSpace(r.Sku) || r.Cost < 0)) return Bad("Every row needs a SKU and a cost of zero or more.");
        return await Guard(id, db, ct, () => PricingService.IngestAsync(db, id, i.Rows.Select(r => new CostRow(r.Sku, r.Description, r.Cost)).ToList(), "manual", ct));
    }

    static Task<IResult> Adjust(int id, AdjustInput i, NpgsqlDataSource db, CancellationToken ct)
        => i.Percent is <= -100 or > 1000 ? Task.FromResult(Bad("Percent must be between -100 and 1000.")) : Guard(id, db, ct, () => PricingService.AdjustAsync(db, id, i.Percent, ct));

    static async Task<IResult> Import(int id, ImportInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Content) || i.Content.Length > 10_000_000) return Bad("Provide file content (10 MB max).");
        string fmt; JsonElement map;
        await using (var c = db.CreateCommand("SELECT feed_format, column_map::text FROM supplier WHERE id = $1"))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            fmt = r.GetString(0); map = JsonDocument.Parse(r.GetString(1)).RootElement.Clone();
        }
        return await Guard(id, db, ct, () => PricingService.IngestAsync(db, id, PricingService.Parse(i.Content, fmt, map), "upload", ct));
    }

    static Task<IResult> RunNow(int id, NpgsqlDataSource db, CancellationToken ct) => Guard(id, db, ct, () => PricingService.RunFeedAsync(db, id, ct));

    /// <summary>Runs a pricing action, mapping expected failures to 400/404 and keeping internals out of the response.</summary>
    static async Task<IResult> Guard(int supplierId, NpgsqlDataSource db, CancellationToken ct, Func<Task<IngestResult>> act)
    {
        try { return Results.Ok(await act()); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (FormatException e) { return Bad(e.Message); }
        catch (JsonException) { return Bad("The content is not valid JSON."); }
        catch (HttpRequestException e) { return Bad("Feed request failed: " + e.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Bad("Feed request timed out."); }
        catch (PostgresException e) when (e.SqlState == "23503") { return Results.NotFound(); }
    }

    static async Task<IngestResult> Propose(NpgsqlDataSource db, int? supplier, string source, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (b, n) = await PricingService.ProposeAsync(conn, tx, supplier, source, ct);
        await tx.CommitAsync(ct);
        return new IngestResult(0, 0, 0, b, n);
    }

    static async Task<IResult> ListLinks(int? supplierId, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT pt.id, p.code, p.description, pt.thick_num, pt.thick_den, si.id, si.sku, si.unit_cost, pcs.factor, si.supplier_id
            FROM part_cost_source pcs JOIN part_thickness pt ON pt.id = pcs.part_thickness_id JOIN part p ON p.id = pt.part_id
            JOIN supplier_item si ON si.id = pcs.supplier_item_id
            WHERE $1::int IS NULL OR si.supplier_id = $1::int ORDER BY p.code, pt.thick_num::float / nullif(pt.thick_den, 0) LIMIT 1000
            """);
        c.Parameters.AddWithValue((object?)supplierId ?? DBNull.Value);
        var list = new List<object>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { thicknessId = r.GetInt32(0), partCode = r.GetString(1), partDescription = r.IsDBNull(2) ? null : r.GetString(2), thickNum = r.GetInt16(3), thickDen = r.GetInt16(4),
                           supplierItemId = r.GetInt32(5), sku = r.GetString(6), unitCost = r.GetDecimal(7), factor = r.GetDecimal(8), supplierId = r.GetInt32(9) });
        return Results.Ok(list);
    }

    static async Task<IResult> SearchThicknesses(string? q, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT pt.id, p.code, p.description, pt.thick_num, pt.thick_den, pcs.supplier_item_id IS NOT NULL
            FROM part_thickness pt JOIN part p ON p.id = pt.part_id LEFT JOIN part_cost_source pcs ON pcs.part_thickness_id = pt.id
            WHERE $1::text IS NULL OR p.code ILIKE $1 OR p.description ILIKE $1
            ORDER BY p.code, pt.thick_num::float / nullif(pt.thick_den, 0) LIMIT 100
            """);
        c.Parameters.AddWithValue(string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + Customers.CustomerEndpoints.EscapeLike(q.Trim()) + "%");
        var list = new List<object>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { thicknessId = r.GetInt32(0), partCode = r.GetString(1), partDescription = r.IsDBNull(2) ? null : r.GetString(2), thickNum = r.GetInt16(3), thickDen = r.GetInt16(4), linked = r.GetBoolean(5) });
        return Results.Ok(list);
    }

    static async Task<IResult> PutLink(int thicknessId, LinkInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (i.Factor <= 0) return Bad("Factor must be greater than zero.");
        await using var c = db.CreateCommand("""
            INSERT INTO part_cost_source (part_thickness_id, supplier_item_id, factor) VALUES ($1,$2,$3)
            ON CONFLICT (part_thickness_id) DO UPDATE SET supplier_item_id = EXCLUDED.supplier_item_id, factor = EXCLUDED.factor
            """);
        c.Parameters.AddWithValue(thicknessId); c.Parameters.AddWithValue(i.SupplierItemId); c.Parameters.AddWithValue(i.Factor);
        try { await c.ExecuteNonQueryAsync(ct); return Results.NoContent(); }
        catch (PostgresException e) when (e.SqlState == "23503") { return Bad("Unknown thickness or supplier item."); }
    }

    static async Task<IResult> Markups(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT pc.code, pc.description, pc.markup_pct,
                   (SELECT count(*) FROM part_price pp JOIN part_cost_source s ON s.part_thickness_id = pp.part_thickness_id WHERE pp.price_category = pc.code)
            FROM price_category pc ORDER BY pc.code
            """);
        var list = new List<object>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { code = r.GetString(0), description = r.IsDBNull(1) ? null : r.GetString(1), markupPct = r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2), linkedPrices = r.GetInt64(3) });
        return Results.Ok(list);
    }

    static async Task<IResult> PutMarkup(string code, MarkupInput i, NpgsqlDataSource db, CancellationToken ct)
    {
        if (i.MarkupPct is <= -100 or > 10000) return Bad("Markup must be above -100 and at most 10000.");
        await using var c = db.CreateCommand("UPDATE price_category SET markup_pct = $2 WHERE code = $1");
        c.Parameters.AddWithValue(code); c.Parameters.AddWithValue((object?)i.MarkupPct ?? DBNull.Value);
        if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        return Results.Ok(await Propose(db, null, "markup", ct));   // the new markup may change many prices: propose, never apply
    }

    static async Task<IResult> Batches(string? status, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT b.id, b.created_at, b.source, b.status, b.decided_at, s.name, u.code, (SELECT count(*) FROM price_change x WHERE x.batch_id = b.id)
            FROM price_change_batch b LEFT JOIN supplier s ON s.id = b.supplier_id LEFT JOIN app_user u ON u.id = b.decided_by
            WHERE $1::text IS NULL OR b.status = $1 ORDER BY b.created_at DESC LIMIT 100
            """);
        c.Parameters.AddWithValue(string.IsNullOrWhiteSpace(status) ? DBNull.Value : status);
        var list = new List<object>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { id = r.GetInt32(0), createdAt = r.GetDateTime(1), source = r.GetString(2), status = r.GetString(3), decidedAt = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4),
                           supplier = r.IsDBNull(5) ? null : r.GetString(5), decidedBy = r.IsDBNull(6) ? null : r.GetString(6), changeCount = r.GetInt64(7) });
        return Results.Ok(list);
    }

    static async Task<IResult> BatchDetail(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT p.code, p.description, pt.thick_num, pt.thick_den, pp.price_category, x.old_price, x.new_price, x.cost, x.markup_pct
            FROM price_change x JOIN part_price pp ON pp.id = x.part_price_id JOIN part_thickness pt ON pt.id = pp.part_thickness_id JOIN part p ON p.id = pt.part_id
            WHERE x.batch_id = $1 ORDER BY p.code, pt.thick_num::float / nullif(pt.thick_den, 0), pp.price_category
            """);
        c.Parameters.AddWithValue(id);
        var list = new List<object>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { partCode = r.GetString(0), partDescription = r.IsDBNull(1) ? null : r.GetString(1), thickNum = r.GetInt16(2), thickDen = r.GetInt16(3),
                           category = r.IsDBNull(4) ? null : r.GetString(4), oldPrice = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5), newPrice = r.GetDecimal(6), cost = r.GetDecimal(7), markupPct = r.GetDecimal(8) });
        return Results.Ok(list);
    }

    static async Task<IResult> Decide(int id, bool approve, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var st = new NpgsqlCommand("UPDATE price_change_batch SET status = $2, decided_by = $3, decided_at = now() WHERE id = $1 AND status = 'pending'", conn, tx);
        st.Parameters.AddWithValue(id); st.Parameters.AddWithValue(approve ? "applied" : "rejected"); st.Parameters.AddWithValue(user.UserId());
        if (await st.ExecuteNonQueryAsync(ct) == 0) return Results.Conflict(new { error = "That batch is no longer pending." });
        var applied = 0;
        if (approve)
        {
            await using var up = new NpgsqlCommand("UPDATE part_price pp SET price1 = x.new_price FROM price_change x WHERE x.batch_id = $1 AND pp.id = x.part_price_id", conn, tx);
            up.Parameters.AddWithValue(id); applied = await up.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { status = approve ? "applied" : "rejected", pricesUpdated = applied });
    }
}
