using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Inventory;

public record InvStatusBody(bool On);
public record ItemBody(int? SupplierItemId, string? Unit, decimal? ReorderPoint, decimal? OrderMultiple, int? LeadDays, string? Location, bool? Active, decimal? OnHand);
public record AdjustBody(decimal? Qty, decimal? SetTo, string? Note);
public record SupplierContactBody(string? Email, string? Phone, string? OrderNotes);
public record PoLineBody(int ItemId, decimal Qty);
public record PoBody(int? SupplierId, string? Note, string? ExpectedDate, List<PoLineBody>? Lines);
public record ReceiveLine(int LineId, decimal Qty);
public record ReceiveBody(List<ReceiveLine>? Lines);
public record FromNeedsBody(int? SupplierId, int? CoverDays);

/// <summary>
/// Inventory window API. 'inventory.manage' for everything except the on/off state and the per-document stock check (any reader of paperwork).
/// While inventory is switched off every route except the switch answers 409 and nothing in the program touches stock.
/// </summary>
public static class InventoryEndpoints
{
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static object? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/inventory");
        g.MapGet("/status", async (NpgsqlDataSource db, CancellationToken ct) => Results.Ok(new { on = await Stock.IsOn(db, ct) }));
        g.MapPut("/status", async (InvStatusBody b, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("""
                INSERT INTO system_state (key, value, updated_at) VALUES ('inventory', jsonb_build_object('on', $1::boolean), now())
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                """);
            c.Parameters.AddWithValue(b.On); await c.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { on = b.On });
        }).RequirePermission("inventory.manage");
        g.MapGet("/document/{id:int}", DocumentCheck).RequirePermission("documents.read");

        var h = g.MapGroup("").RequirePermission("inventory.manage");
        h.AddEndpointFilter(async (ctx, next) => await Stock.IsOn(ctx.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>(), ctx.HttpContext.RequestAborted)
            ? await next(ctx) : Results.Json(new { error = "Inventory is turned off. Turn it on in the Inventory window first." }, statusCode: 409));
        h.MapGet("/items", async (NpgsqlDataSource db, CancellationToken ct) => Results.Ok((await Snapshot(db, 14, ct)).Select(Row)));
        h.MapGet("/catalog", Catalog);
        h.MapPost("/items", AddItem);
        h.MapPut("/items/{id:int}", EditItem);
        h.MapPost("/items/{id:int}/adjust", Adjust);
        h.MapGet("/items/{id:int}/moves", Moves);
        h.MapGet("/needs", Needs);
        h.MapGet("/suppliers", Suppliers);
        h.MapPut("/suppliers/{id:int}", SaveSupplier);
        h.MapGet("/orders", Orders);
        h.MapGet("/orders/{id:int}", GetOrder);
        h.MapPost("/orders", CreateOrder);
        h.MapPost("/orders/from-needs", FromNeeds);
        h.MapPut("/orders/{id:int}", EditOrder);
        h.MapDelete("/orders/{id:int}", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("DELETE FROM purchase_order WHERE id = $1 AND status = 'draft'"); c.Parameters.AddWithValue(id);
            return await c.ExecuteNonQueryAsync(ct) > 0 ? Results.NoContent() : Results.Conflict(new { error = "Only a draft order can be deleted. Cancel it instead." });
        });
        h.MapPost("/orders/{id:int}/sent", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("UPDATE purchase_order SET status = 'sent', sent_at = now() WHERE id = $1 AND status = 'draft' AND EXISTS (SELECT 1 FROM purchase_order_line WHERE po_id = $1)");
            c.Parameters.AddWithValue(id);
            return await c.ExecuteNonQueryAsync(ct) > 0 ? Results.NoContent() : Results.Conflict(new { error = "Only a draft order with at least one line can be marked as sent." });
        });
        h.MapPost("/orders/{id:int}/receive", Receive);
        h.MapPost("/orders/{id:int}/cancel", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
        {   // nothing received: cancelled. Part received: closed short (counts as received so the rest is no longer "on order").
            await using var c = db.CreateCommand("""
                UPDATE purchase_order p SET status = CASE WHEN EXISTS (SELECT 1 FROM purchase_order_line WHERE po_id = p.id AND received_qty > 0) THEN 'received' ELSE 'cancelled' END
                WHERE id = $1 AND status IN ('draft', 'sent', 'partial')
                """);
            c.Parameters.AddWithValue(id);
            return await c.ExecuteNonQueryAsync(ct) > 0 ? Results.NoContent() : Results.Conflict(new { error = "This order is already finished." });
        });
    }

    // ---------- stock ----------

    record Snap(int Id, int SupplierId, string Supplier, string Sku, string? Description, decimal UnitCost, string Unit, decimal ReorderPoint, decimal Multiple, int LeadDays,
        string? Location, bool Active, decimal OnHand, decimal Committed, decimal Quoted, decimal OnOrder, decimal Daily, decimal Want, decimal Suggest);

    /// <summary>
    /// Every tracked item with its numbers. Committed = open work orders; Quoted = open quotes from the last 60 days times the share of quotes that became jobs;
    /// Daily = average use per day over 90 days. Want = what we should hold (the larger of the reorder point and demand + cover for lead time and CoverDays)
    /// less what is on hand or already ordered; Suggest rounds a positive Want up to the order multiple.
    /// </summary>
    static async Task<List<Snap>> Snapshot(NpgsqlDataSource db, int coverDays, CancellationToken ct, int? only = null)
    {
        decimal win;
        await using (var c = db.CreateCommand("""
            SELECT count(*), count(*) FILTER (WHERE EXISTS (SELECT 1 FROM document w WHERE w.source_doc_number = doc_number_text(q.doc_prefix, q.doc_number, q.doc_suffix) AND w.company_id = q.company_id AND w.doc_type IN ('W', 'I') AND NOT w.voided))
            FROM document q WHERE q.doc_type = 'Q' AND NOT q.voided AND q.doc_date >= current_date - 365 AND q.doc_date < current_date - 60
            """))
        {
            await using var r = await c.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
            win = r.GetInt64(0) >= 10 ? Math.Round((decimal)r.GetInt64(1) / r.GetInt64(0), 3) : 0.5m;   // too few quotes to know: assume half
        }
        await using var cmd = db.CreateCommand($"""
            WITH committed AS (SELECT item_id, sum(q) q FROM ({Stock.Demand("d.doc_type = 'W'")}) x GROUP BY item_id),
                 quoted AS (SELECT item_id, sum(q) q FROM ({Stock.Demand("""
                    d.doc_type = 'Q' AND d.doc_date >= current_date - 60 AND NOT EXISTS (SELECT 1 FROM document w WHERE w.source_doc_number = doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix)
                        AND w.company_id = d.company_id AND w.doc_type IN ('W', 'I') AND NOT w.voided)
                    """)}) x GROUP BY item_id),
                 onhand AS (SELECT item_id, sum(qty) q FROM inventory_move GROUP BY item_id),
                 used AS (SELECT item_id, -sum(qty) q FROM inventory_move WHERE kind = 'use' AND at >= now() - interval '90 days' GROUP BY item_id),
                 ordered AS (SELECT l.item_id, sum(l.qty - l.received_qty) q FROM purchase_order_line l JOIN purchase_order p ON p.id = l.po_id
                             WHERE p.status IN ('draft', 'sent', 'partial') GROUP BY l.item_id)
            SELECT i.id, s.id, s.name, si.sku, si.description, si.unit_cost, i.unit, i.reorder_point, i.order_multiple, i.lead_days, i.location, i.active,
                   coalesce(oh.q, 0), coalesce(cm.q, 0), coalesce(qt.q, 0) * $1, coalesce(od.q, 0), coalesce(us.q, 0) / 90.0
            FROM inventory_item i JOIN supplier_item si ON si.id = i.supplier_item_id JOIN supplier s ON s.id = si.supplier_id
            LEFT JOIN onhand oh ON oh.item_id = i.id LEFT JOIN committed cm ON cm.item_id = i.id LEFT JOIN quoted qt ON qt.item_id = i.id
            LEFT JOIN used us ON us.item_id = i.id LEFT JOIN ordered od ON od.item_id = i.id
            WHERE $2::int IS NULL OR i.id = $2
            ORDER BY lower(s.name), lower(si.sku)
            """);
        cmd.Parameters.AddWithValue(win); cmd.Parameters.AddWithValue((object?)only ?? DBNull.Value);
        var list = new List<Snap>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            decimal onHand = rd.GetDecimal(12), committed = rd.GetDecimal(13), quoted = Math.Round(rd.GetDecimal(14), 3), onOrder = rd.GetDecimal(15), daily = rd.GetDecimal(16);
            decimal point = rd.GetDecimal(7), multiple = rd.GetDecimal(8); int lead = rd.GetInt32(9);
            var hold = Math.Max(point, committed + quoted + daily * (lead + coverDays));
            var want = Math.Round(hold - onHand - onOrder, 3);
            var suggest = want > 0 ? Math.Ceiling(want / multiple) * multiple : 0;
            list.Add(new Snap(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4), rd.GetDecimal(5), rd.GetString(6), point, multiple, lead,
                rd.IsDBNull(10) ? null : rd.GetString(10), rd.GetBoolean(11), onHand, committed, quoted, onOrder, Math.Round(daily, 4), want, suggest));
        }
        return list;
    }

    static object Row(Snap s) => new
    {
        id = s.Id, supplierId = s.SupplierId, supplier = s.Supplier, sku = s.Sku, description = s.Description, unitCost = s.UnitCost, unit = s.Unit,
        reorderPoint = s.ReorderPoint, orderMultiple = s.Multiple, leadDays = s.LeadDays, location = s.Location, active = s.Active,
        onHand = s.OnHand, committed = s.Committed, quoted = s.Quoted, onOrder = s.OnOrder, perDay = s.Daily, available = s.OnHand - s.Committed, suggest = s.Suggest,
    };

    static async Task<IResult> Needs(int? coverDays, NpgsqlDataSource db, CancellationToken ct)
    {
        var cover = Math.Clamp(coverDays ?? 14, 0, 365);
        var rows = (await Snapshot(db, cover, ct)).Where(s => s.Active && s.Suggest > 0).ToList();
        return Results.Ok(new { coverDays = cover, items = rows.Select(s => new { id = s.Id, supplierId = s.SupplierId, supplier = s.Supplier, sku = s.Sku, description = s.Description, unit = s.Unit, unitCost = s.UnitCost,
            onHand = s.OnHand, committed = s.Committed, quoted = s.Quoted, onOrder = s.OnOrder, perDay = s.Daily, reorderPoint = s.ReorderPoint, suggest = s.Suggest, cost = Math.Round(s.Suggest * s.UnitCost, 2) }) });
    }

    static async Task<IResult> Catalog(string? q, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT si.id, s.name, si.sku, si.description, si.unit_cost, (SELECT count(*) FROM part_cost_source p WHERE p.supplier_item_id = si.id), i.id IS NOT NULL
            FROM supplier_item si JOIN supplier s ON s.id = si.supplier_id LEFT JOIN inventory_item i ON i.supplier_item_id = si.id
            WHERE $1::text IS NULL OR si.sku ILIKE $1 OR si.description ILIKE $1 OR s.name ILIKE $1
            ORDER BY (i.id IS NOT NULL), lower(s.name), lower(si.sku) LIMIT 100
            """);
        c.Parameters.AddWithValue(string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + Customers.CustomerEndpoints.EscapeLike(q.Trim()) + "%");
        var list = new List<object>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new { supplierItemId = r.GetInt32(0), supplier = r.GetString(1), sku = r.GetString(2), description = D(r, 3), unitCost = r.GetDecimal(4), links = r.GetInt64(5), tracked = r.GetBoolean(6) });
        return Results.Ok(list);
    }

    static async Task<IResult> AddItem(ItemBody b, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (b.SupplierItemId is null) return Bad("Choose a supplier item.");
        if (b.ReorderPoint is < 0 || b.OrderMultiple is <= 0 || b.LeadDays is < 0 or > 365 || b.OnHand is < 0) return Bad("Check the numbers: none can be negative, and the order multiple must be above zero.");
        await using var cn = await db.OpenConnectionAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        int id;
        await using (var c = new NpgsqlCommand("INSERT INTO inventory_item (supplier_item_id, unit, reorder_point, order_multiple, lead_days, location) VALUES ($1, coalesce(nullif($2, ''), 'each'), coalesce($3, 0), coalesce($4, 1), coalesce($5, 7), nullif($6, '')) RETURNING id", cn, tx))
        {
            c.Parameters.AddWithValue(b.SupplierItemId.Value); c.Parameters.AddWithValue((object?)b.Unit?.Trim() ?? DBNull.Value); c.Parameters.AddWithValue((object?)b.ReorderPoint ?? DBNull.Value);
            c.Parameters.AddWithValue((object?)b.OrderMultiple ?? DBNull.Value); c.Parameters.AddWithValue((object?)b.LeadDays ?? DBNull.Value); c.Parameters.AddWithValue((object?)b.Location?.Trim() ?? DBNull.Value);
            try { id = (int)(await c.ExecuteScalarAsync(ct))!; }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "That item is already tracked." }); }
            catch (PostgresException e) when (e.SqlState == "23503") { return Bad("That supplier item does not exist."); }
        }
        if (b.OnHand is > 0)
        {
            await using var m = new NpgsqlCommand("INSERT INTO inventory_move (item_id, qty, kind, note, user_id) VALUES ($1, $2, 'count', 'Starting count', $3)", cn, tx);
            m.Parameters.AddWithValue(id); m.Parameters.AddWithValue(b.OnHand.Value); m.Parameters.AddWithValue((short)user.UserId()); await m.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { id });
    }

    static async Task<IResult> EditItem(int id, ItemBody b, NpgsqlDataSource db, CancellationToken ct)
    {
        if (b.ReorderPoint is < 0 || b.OrderMultiple is <= 0 || b.LeadDays is < 0 or > 365) return Bad("Check the numbers: none can be negative, and the order multiple must be above zero.");
        await using var c = db.CreateCommand("""
            UPDATE inventory_item SET unit = coalesce(nullif($2, ''), unit), reorder_point = coalesce($3, reorder_point), order_multiple = coalesce($4, order_multiple),
                   lead_days = coalesce($5, lead_days), location = CASE WHEN $6::text IS NULL THEN location ELSE nullif($6, '') END, active = coalesce($7, active) WHERE id = $1
            """);
        c.Parameters.AddWithValue(id); c.Parameters.AddWithValue((object?)b.Unit?.Trim() ?? DBNull.Value); c.Parameters.AddWithValue((object?)b.ReorderPoint ?? DBNull.Value);
        c.Parameters.AddWithValue((object?)b.OrderMultiple ?? DBNull.Value); c.Parameters.AddWithValue((object?)b.LeadDays ?? DBNull.Value);
        c.Parameters.AddWithValue((object?)b.Location?.Trim() ?? DBNull.Value); c.Parameters.AddWithValue((object?)b.Active ?? DBNull.Value);
        return await c.ExecuteNonQueryAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }

    static async Task<IResult> Adjust(int id, AdjustBody b, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if ((b.Qty is null) == (b.SetTo is null)) return Bad("Give either an amount to add or remove, or the counted amount.");
        if (b.SetTo is < 0) return Bad("A count cannot be negative.");
        if (b.Qty == 0) return Bad("The amount cannot be zero.");
        await using var c = db.CreateCommand("""
            INSERT INTO inventory_move (item_id, qty, kind, note, user_id)
            SELECT i.id, CASE WHEN $2::numeric IS NULL THEN $3 ELSE $2 - coalesce((SELECT sum(qty) FROM inventory_move WHERE item_id = i.id), 0) END,
                   CASE WHEN $2::numeric IS NULL THEN 'adjust' ELSE 'count' END, nullif($4, ''), $5
            FROM inventory_item i WHERE i.id = $1 RETURNING qty
            """);
        c.Parameters.AddWithValue(id); c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = (object?)b.SetTo ?? DBNull.Value });
        c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = (object?)b.Qty ?? DBNull.Value });
        c.Parameters.AddWithValue((object?)b.Note?.Trim() ?? DBNull.Value); c.Parameters.AddWithValue((short)user.UserId());
        return await c.ExecuteScalarAsync(ct) is null ? Results.NotFound() : Results.NoContent();
    }

    static async Task<IResult> Moves(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT m.at, m.qty, m.kind, m.note, u.code, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), m.po_id
            FROM inventory_move m LEFT JOIN app_user u ON u.id = m.user_id LEFT JOIN document d ON d.id = m.document_id
            WHERE m.item_id = $1 ORDER BY m.at DESC, m.id DESC LIMIT 200
            """);
        c.Parameters.AddWithValue(id);
        var list = new List<object>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new { at = r.GetDateTime(0), qty = r.GetDecimal(1), kind = r.GetString(2), note = D(r, 3), user = D(r, 4), document = D(r, 5), poId = D(r, 6) });
        return Results.Ok(list);
    }

    /// <summary>What a quote, work order or invoice needs from stock, and whether stock can cover it together with all open work orders.</summary>
    static async Task<IResult> DocumentCheck(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        if (!await Stock.IsOn(db, ct)) return Results.Ok(new { on = false, lines = Array.Empty<object>() });
        var snaps = (await Snapshot(db, 14, ct)).ToDictionary(s => s.Id);
        string type;
        await using (var c = db.CreateCommand("SELECT doc_type FROM document WHERE id = $1")) { c.Parameters.AddWithValue(id); if (await c.ExecuteScalarAsync(ct) is not string t) return Results.NotFound(); type = t.Trim(); }
        await using var q = db.CreateCommand(Stock.Demand("d.id = $1")); q.Parameters.AddWithValue(id);
        var lines = new List<object>(); await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var s = snaps[r.GetInt32(0)]; var need = Math.Round(r.GetDecimal(2), 3);
            var demand = s.Committed + (type == "W" ? 0 : need);
            lines.Add(new { sku = s.Sku, description = s.Description, unit = s.Unit, need, onHand = s.OnHand, committed = s.Committed, onOrder = s.OnOrder, shortBy = Math.Max(0, Math.Round(demand - s.OnHand, 3)) });
        }
        return Results.Ok(new { on = true, type, lines });
    }

    // ---------- suppliers ----------

    static async Task<IResult> Suppliers(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT id, name, email, phone, order_notes FROM supplier WHERE active ORDER BY lower(name)");
        var list = new List<object>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new { id = r.GetInt32(0), name = r.GetString(1), email = D(r, 2), phone = D(r, 3), orderNotes = D(r, 4) });
        return Results.Ok(list);
    }

    static async Task<IResult> SaveSupplier(int id, SupplierContactBody b, NpgsqlDataSource db, CancellationToken ct)
    {
        if (b.Email is { Length: > 0 } e && (e.Length > 200 || !e.Contains('@') || e.Any(char.IsWhiteSpace))) return Bad("That does not look like an email address.");
        await using var c = db.CreateCommand("UPDATE supplier SET email = nullif($2, ''), phone = nullif($3, ''), order_notes = nullif($4, '') WHERE id = $1");
        c.Parameters.AddWithValue(id); c.Parameters.AddWithValue((object?)b.Email?.Trim() ?? ""); c.Parameters.AddWithValue((object?)b.Phone?.Trim() ?? ""); c.Parameters.AddWithValue((object?)b.OrderNotes?.Trim() ?? "");
        return await c.ExecuteNonQueryAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }

    // ---------- purchase orders ----------

    static async Task<IResult> Orders(string? status, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT p.id, s.name, p.status, p.created_at, p.sent_at, p.expected_date, (SELECT count(*) FROM purchase_order_line l WHERE l.po_id = p.id),
                   (SELECT coalesce(sum(l.qty * l.unit_cost), 0) FROM purchase_order_line l WHERE l.po_id = p.id)
            FROM purchase_order p JOIN supplier s ON s.id = p.supplier_id
            WHERE ($1::text IS NULL AND p.status IN ('draft', 'sent', 'partial')) OR p.status = $1 OR $1 = 'all'
            ORDER BY p.id DESC LIMIT 300
            """);
        c.Parameters.AddWithValue((object?)status ?? DBNull.Value);
        var list = new List<object>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new { id = r.GetInt32(0), supplier = r.GetString(1), status = r.GetString(2), createdAt = r.GetDateTime(3), sentAt = D(r, 4), expectedDate = D(r, 5), lines = r.GetInt64(6), total = r.GetDecimal(7) });
        return Results.Ok(list);
    }

    static async Task<IResult> GetOrder(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        object head;
        await using (var c = db.CreateCommand("""
            SELECT p.id, p.status, p.created_at, p.sent_at, p.expected_date, p.note, s.id, s.name, s.email, s.phone, s.order_notes, (SELECT name FROM company ORDER BY id LIMIT 1), (SELECT phone FROM company ORDER BY id LIMIT 1)
            FROM purchase_order p JOIN supplier s ON s.id = p.supplier_id WHERE p.id = $1
            """))
        {
            c.Parameters.AddWithValue(id); await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            head = new { id = r.GetInt32(0), status = r.GetString(1), createdAt = r.GetDateTime(2), sentAt = D(r, 3), expectedDate = D(r, 4), note = D(r, 5),
                supplierId = r.GetInt32(6), supplier = r.GetString(7), supplierEmail = D(r, 8), supplierPhone = D(r, 9), supplierNotes = D(r, 10), company = D(r, 11), companyPhone = D(r, 12) };
        }
        await using var lc = db.CreateCommand("""
            SELECT l.id, l.item_id, si.sku, si.description, i.unit, l.qty, l.unit_cost, l.received_qty
            FROM purchase_order_line l JOIN inventory_item i ON i.id = l.item_id JOIN supplier_item si ON si.id = i.supplier_item_id WHERE l.po_id = $1 ORDER BY l.id
            """);
        lc.Parameters.AddWithValue(id);
        var lines = new List<object>(); await using var lr = await lc.ExecuteReaderAsync(ct);
        while (await lr.ReadAsync(ct)) lines.Add(new { id = lr.GetInt32(0), itemId = lr.GetInt32(1), sku = lr.GetString(2), description = D(lr, 3), unit = lr.GetString(4), qty = lr.GetDecimal(5), unitCost = lr.GetDecimal(6), received = lr.GetDecimal(7) });
        return Results.Ok(new { order = head, lines });
    }

    static async Task<IResult> InsertLines(NpgsqlConnection cn, NpgsqlTransaction tx, int poId, int supplierId, IEnumerable<PoLineBody> lines, CancellationToken ct)
    {
        foreach (var l in lines.GroupBy(x => x.ItemId).Select(g => new PoLineBody(g.Key, g.Sum(x => x.Qty))))
        {
            if (l.Qty <= 0 || l.Qty > 1_000_000) return Bad("Every line needs a quantity above zero.");
            await using var c = new NpgsqlCommand("""
                INSERT INTO purchase_order_line (po_id, item_id, qty, unit_cost)
                SELECT $1, i.id, $3, si.unit_cost FROM inventory_item i JOIN supplier_item si ON si.id = i.supplier_item_id WHERE i.id = $2 AND si.supplier_id = $4
                """, cn, tx);
            c.Parameters.AddWithValue(poId); c.Parameters.AddWithValue(l.ItemId); c.Parameters.AddWithValue(l.Qty); c.Parameters.AddWithValue(supplierId);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Bad("A line is for an item that does not belong to this supplier.");
        }
        return Results.Ok();
    }

    static async Task<IResult> CreateOrder(PoBody b, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (b.SupplierId is null) return Bad("Choose a supplier.");
        if (b.Lines is not { Count: > 0 }) return Bad("Add at least one line.");
        if (b.ExpectedDate is { Length: > 0 } && !DateOnly.TryParse(b.ExpectedDate, out _)) return Bad("The expected date is not a valid date.");
        await using var cn = await db.OpenConnectionAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        int id;
        await using (var c = new NpgsqlCommand("INSERT INTO purchase_order (supplier_id, created_by, note, expected_date) VALUES ($1, $2, nullif($3, ''), $4) RETURNING id", cn, tx))
        {
            c.Parameters.AddWithValue(b.SupplierId.Value); c.Parameters.AddWithValue((short)user.UserId()); c.Parameters.AddWithValue((object?)b.Note?.Trim() ?? "");
            c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = b.ExpectedDate is { Length: > 0 } e ? DateOnly.Parse(e) : DBNull.Value });
            try { id = (int)(await c.ExecuteScalarAsync(ct))!; } catch (PostgresException e2) when (e2.SqlState == "23503") { return Bad("That supplier does not exist."); }
        }
        var res = await InsertLines(cn, tx, id, b.SupplierId.Value, b.Lines, ct);
        if (res is not Microsoft.AspNetCore.Http.HttpResults.Ok) return res;
        await tx.CommitAsync(ct);
        return Results.Ok(new { id });
    }

    /// <summary>One draft order per supplier from the needs list. Items already on an open order are not suggested again, so pressing this twice does not double up.</summary>
    static async Task<IResult> FromNeeds(FromNeedsBody b, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        var rows = (await Snapshot(db, Math.Clamp(b.CoverDays ?? 14, 0, 365), ct)).Where(s => s.Active && s.Suggest > 0 && (b.SupplierId is null || s.SupplierId == b.SupplierId)).ToList();
        if (rows.Count == 0) return Results.Ok(new { created = Array.Empty<int>() });
        await using var cn = await db.OpenConnectionAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        var made = new List<int>();
        foreach (var grp in rows.GroupBy(r => r.SupplierId))
        {
            int id;
            await using (var c = new NpgsqlCommand("INSERT INTO purchase_order (supplier_id, created_by, note) VALUES ($1, $2, 'From the needs estimate') RETURNING id", cn, tx))
            { c.Parameters.AddWithValue(grp.Key); c.Parameters.AddWithValue((short)user.UserId()); id = (int)(await c.ExecuteScalarAsync(ct))!; }
            var res = await InsertLines(cn, tx, id, grp.Key, grp.Select(r => new PoLineBody(r.Id, r.Suggest)), ct);
            if (res is not Microsoft.AspNetCore.Http.HttpResults.Ok) return res;
            made.Add(id);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { created = made });
    }

    static async Task<IResult> EditOrder(int id, PoBody b, NpgsqlDataSource db, CancellationToken ct)
    {
        if (b.ExpectedDate is { Length: > 0 } && !DateOnly.TryParse(b.ExpectedDate, out _)) return Bad("The expected date is not a valid date.");
        await using var cn = await db.OpenConnectionAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        int supplier;
        await using (var c = new NpgsqlCommand("SELECT supplier_id FROM purchase_order WHERE id = $1 AND status = 'draft' FOR UPDATE", cn, tx))
        {
            c.Parameters.AddWithValue(id);
            if (await c.ExecuteScalarAsync(ct) is not int s) return Results.Conflict(new { error = "Only a draft order can be changed." });
            supplier = s;
        }
        await using (var c = new NpgsqlCommand("UPDATE purchase_order SET note = nullif($2, ''), expected_date = $3 WHERE id = $1", cn, tx))
        {
            c.Parameters.AddWithValue(id); c.Parameters.AddWithValue((object?)b.Note?.Trim() ?? "");
            c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = b.ExpectedDate is { Length: > 0 } e ? DateOnly.Parse(e) : DBNull.Value });
            await c.ExecuteNonQueryAsync(ct);
        }
        if (b.Lines is not null)
        {
            await using (var d = new NpgsqlCommand("DELETE FROM purchase_order_line WHERE po_id = $1", cn, tx)) { d.Parameters.AddWithValue(id); await d.ExecuteNonQueryAsync(ct); }
            var res = await InsertLines(cn, tx, id, supplier, b.Lines, ct);
            if (res is not Microsoft.AspNetCore.Http.HttpResults.Ok) return res;
        }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> Receive(int id, ReceiveBody b, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (b.Lines is not { Count: > 0 } || b.Lines.Any(l => l.Qty <= 0)) return Bad("Enter the quantity received on at least one line.");
        await using var cn = await db.OpenConnectionAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("SELECT status FROM purchase_order WHERE id = $1 FOR UPDATE", cn, tx))
        {
            c.Parameters.AddWithValue(id);
            if (await c.ExecuteScalarAsync(ct) is not string st) return Results.NotFound();
            if (st is "received" or "cancelled") return Results.Conflict(new { error = "This order is already finished." });
        }
        foreach (var l in b.Lines)
        {
            await using var c = new NpgsqlCommand("""
                WITH u AS (UPDATE purchase_order_line SET received_qty = received_qty + $3 WHERE id = $2 AND po_id = $1 RETURNING item_id)
                INSERT INTO inventory_move (item_id, qty, kind, po_id, note, user_id) SELECT item_id, $3, 'receive', $1, 'Received on PO-' || lpad($1::text, 5, '0'), $4 FROM u
                """, cn, tx);
            c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(l.LineId); c.Parameters.AddWithValue(l.Qty); c.Parameters.AddWithValue((short)user.UserId());
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Bad("A line does not belong to this order.");
        }
        await using (var c = new NpgsqlCommand("""
            UPDATE purchase_order p SET status = CASE WHEN NOT EXISTS (SELECT 1 FROM purchase_order_line l WHERE l.po_id = p.id AND l.received_qty < l.qty) THEN 'received' ELSE 'partial' END,
                   sent_at = coalesce(sent_at, now()) WHERE id = $1
            """, cn, tx))
        { c.Parameters.AddWithValue(id); await c.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
