using Npgsql;

namespace ReflectoPatronus.Api.Inventory;

/// <summary>
/// Shared rules for inventory: the on/off switch, and how document lines turn into stock needs.
/// A glass or part line needs (qty x uom_units x factor) of the supplier item linked to its part and thickness
/// (uom_units is the area of one piece for glass and 1 for parts; factor converts our price unit to the supplier's unit, e.g. 1/40 per sheet).
/// Lines that are not linked to a supplier item, labor and custom lines never touch stock.
/// </summary>
public static class Stock
{
    public static async Task<bool> IsOn(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT coalesce((value ->> 'on')::boolean, false) FROM system_state WHERE key = 'inventory'");
        return await c.ExecuteScalarAsync(ct) is true;
    }
    public static async Task<bool> IsOn(NpgsqlConnection cn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT coalesce((value ->> 'on')::boolean, false) FROM system_state WHERE key = 'inventory'", cn, tx);
        return await c.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Per inventory item and document: the quantity the document's lines need. <paramref name="where"/> filters on d (document) and l (line).</summary>
    public static string Demand(string where) => $"""
        SELECT ii.id AS item_id, d.id AS document_id, sum(l.qty * coalesce(l.uom_units, 1) * pcs.factor) AS q
        FROM document_line l
        JOIN document d ON d.id = l.document_id
        JOIN part p ON upper(p.code) = upper(l.part_code)
        JOIN part_thickness pt ON pt.part_id = p.id AND pt.thick_num = coalesce(l.thick_num, 0) AND pt.thick_den = coalesce(l.thick_den, 0)
        JOIN part_cost_source pcs ON pcs.part_thickness_id = pt.id
        JOIN inventory_item ii ON ii.supplier_item_id = pcs.supplier_item_id AND ii.active
        WHERE l.source IN ('glass', 'part') AND l.qty > 0 AND NOT d.voided AND ({where})
        GROUP BY ii.id, d.id
        """;

    /// <summary>Called inside the transaction that makes an invoice. Does nothing when inventory is off. Safe to call twice.</summary>
    public static async Task UseForInvoice(NpgsqlConnection cn, NpgsqlTransaction tx, int documentId, int userId, CancellationToken ct)
    {
        if (!await IsOn(cn, tx, ct)) return;
        await using var c = new NpgsqlCommand($"""
            INSERT INTO inventory_move (item_id, qty, kind, document_id, note, user_id)
            SELECT x.item_id, -round(x.q, 3), 'use', x.document_id, 'Invoice ' || doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), $2
            FROM ({Demand("d.id = $1")}) x JOIN document d ON d.id = x.document_id WHERE round(x.q, 3) > 0
            ON CONFLICT (item_id, document_id) WHERE kind = 'use' DO NOTHING
            """, cn, tx);
        c.Parameters.AddWithValue(documentId); c.Parameters.AddWithValue((short)userId);
        await c.ExecuteNonQueryAsync(ct);
    }
}
