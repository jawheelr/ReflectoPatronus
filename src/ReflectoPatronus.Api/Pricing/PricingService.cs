using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ReflectoPatronus.Api.Pricing;

public record CostRow(string Sku, string? Description, decimal Cost);
public record IngestResult(int Added, int Changed, int Unchanged, int? BatchId, int PriceChanges);

public static class PricingService
{
    /// <summary>Upserts supplier costs (with history), then proposes price changes as a PENDING batch. Never edits live prices.</summary>
    public static async Task<IngestResult> IngestAsync(NpgsqlDataSource db, int supplierId, IReadOnlyList<CostRow> rows, string source, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int added = 0, changed = 0, same = 0;
        foreach (var row in rows.GroupBy(r => r.Sku.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            await using var up = new NpgsqlCommand("""
                SELECT id, unit_cost FROM supplier_item WHERE supplier_id = $1 AND lower(sku) = lower($2) FOR UPDATE
                """, conn, tx);
            up.Parameters.AddWithValue(supplierId); up.Parameters.AddWithValue(row.Sku.Trim());
            int? id = null; decimal old = 0;
            await using (var r = await up.ExecuteReaderAsync(ct)) { if (await r.ReadAsync(ct)) { id = r.GetInt32(0); old = r.GetDecimal(1); } }
            if (id is null)
            {
                await using var ins = new NpgsqlCommand("INSERT INTO supplier_item (supplier_id, sku, description, unit_cost) VALUES ($1,$2,$3,$4) RETURNING id", conn, tx);
                ins.Parameters.AddWithValue(supplierId); ins.Parameters.AddWithValue(row.Sku.Trim());
                ins.Parameters.AddWithValue((object?)row.Description ?? DBNull.Value); ins.Parameters.AddWithValue(row.Cost);
                id = (int)(await ins.ExecuteScalarAsync(ct))!; added++;
                await History(conn, tx, id.Value, row.Cost, source, ct);
            }
            else if (old != row.Cost)
            {
                await using var upd = new NpgsqlCommand("UPDATE supplier_item SET unit_cost = $2, description = coalesce($3, description), cost_updated_at = now() WHERE id = $1", conn, tx);
                upd.Parameters.AddWithValue(id.Value); upd.Parameters.AddWithValue(row.Cost); upd.Parameters.AddWithValue((object?)row.Description ?? DBNull.Value);
                await upd.ExecuteNonQueryAsync(ct); changed++;
                await History(conn, tx, id.Value, row.Cost, source, ct);
            }
            else same++;
        }
        var (batch, n) = await ProposeAsync(conn, tx, supplierId, source, ct);
        await tx.CommitAsync(ct);
        return new IngestResult(added, changed, same, batch, n);
    }

    /// <summary>Applies a percentage change to every cost of one supplier (e.g. a published surcharge).</summary>
    public static async Task<IngestResult> AdjustAsync(NpgsqlDataSource db, int supplierId, decimal percent, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var hist = new NpgsqlCommand("""
            INSERT INTO supplier_cost_history (supplier_item_id, unit_cost, source)
            SELECT id, round(unit_cost * (1 + $2 / 100), 4), 'adjust' FROM supplier_item WHERE supplier_id = $1
            """, conn, tx);
        hist.Parameters.AddWithValue(supplierId); hist.Parameters.AddWithValue(percent);
        await hist.ExecuteNonQueryAsync(ct);
        await using var upd = new NpgsqlCommand("UPDATE supplier_item SET unit_cost = round(unit_cost * (1 + $2 / 100), 4), cost_updated_at = now() WHERE supplier_id = $1", conn, tx);
        upd.Parameters.AddWithValue(supplierId); upd.Parameters.AddWithValue(percent);
        var count = await upd.ExecuteNonQueryAsync(ct);
        var (batch, n) = await ProposeAsync(conn, tx, supplierId, "adjust", ct);
        await tx.CommitAsync(ct);
        return new IngestResult(0, count, 0, batch, n);
    }

    static async Task History(NpgsqlConnection c, NpgsqlTransaction t, int itemId, decimal cost, string source, CancellationToken ct)
    {
        await using var h = new NpgsqlCommand("INSERT INTO supplier_cost_history (supplier_item_id, unit_cost, source) VALUES ($1,$2,$3)", c, t);
        h.Parameters.AddWithValue(itemId); h.Parameters.AddWithValue(cost); h.Parameters.AddWithValue(source);
        await h.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Compares cost x factor x (1+markup) with current price1 for every linked, markup-managed price row.
    /// Creates one pending batch (null supplier = all suppliers); an older pending batch for the same scope is superseded.</summary>
    public static async Task<(int? Batch, int Changes)> ProposeAsync(NpgsqlConnection c, NpgsqlTransaction t, int? supplierId, string source, CancellationToken ct)
    {
        await using var mk = new NpgsqlCommand("INSERT INTO price_change_batch (supplier_id, source) VALUES ($1::int, $2) RETURNING id", c, t);
        mk.Parameters.AddWithValue((object?)supplierId ?? DBNull.Value); mk.Parameters.AddWithValue(source);
        var batch = (int)(await mk.ExecuteScalarAsync(ct))!;
        await using var ins = new NpgsqlCommand("""
            INSERT INTO price_change (batch_id, part_price_id, old_price, new_price, cost, markup_pct)
            SELECT $1, pp.id, pp.price1, round(si.unit_cost * pcs.factor * (1 + pc.markup_pct / 100), 2),
                   si.unit_cost * pcs.factor, pc.markup_pct
            FROM part_cost_source pcs
            JOIN supplier_item si ON si.id = pcs.supplier_item_id
            JOIN part_price pp ON pp.part_thickness_id = pcs.part_thickness_id
            JOIN price_category pc ON pc.code = pp.price_category AND pc.markup_pct IS NOT NULL
            JOIN supplier s ON s.id = si.supplier_id AND s.active
            WHERE ($2::int IS NULL OR si.supplier_id = $2::int)
              AND pp.price1 IS DISTINCT FROM round(si.unit_cost * pcs.factor * (1 + pc.markup_pct / 100), 2)
            """, c, t);
        ins.Parameters.AddWithValue(batch); ins.Parameters.AddWithValue((object?)supplierId ?? DBNull.Value);
        var n = await ins.ExecuteNonQueryAsync(ct);
        if (n == 0)
        {
            await using var del = new NpgsqlCommand("DELETE FROM price_change_batch WHERE id = $1", c, t);
            del.Parameters.AddWithValue(batch); await del.ExecuteNonQueryAsync(ct);
            return (null, 0);
        }
        await using var sup = new NpgsqlCommand("""
            UPDATE price_change_batch SET status = 'superseded', decided_at = now()
            WHERE status = 'pending' AND id <> $1 AND ($2::int IS NULL OR supplier_id = $2::int)
            """, c, t);
        sup.Parameters.AddWithValue(batch); sup.Parameters.AddWithValue((object?)supplierId ?? DBNull.Value);
        await sup.ExecuteNonQueryAsync(ct);
        return (batch, n);
    }

    // ---- parsing -------------------------------------------------------------------------------------------------

    public static IReadOnlyList<CostRow> Parse(string content, string format, JsonElement columnMap)
    {
        string Col(string k, string d) => columnMap.ValueKind == JsonValueKind.Object && columnMap.TryGetProperty(k, out var v) && v.GetString() is { Length: > 0 } s ? s : d;
        string skuCol = Col("sku", "sku"), costCol = Col("cost", "cost"), descCol = Col("description", "description");
        var rows = new List<Dictionary<string, string>>();
        if (format == "json")
        {
            using var doc = JsonDocument.Parse(content);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("items", out var it) ? it : throw new FormatException("JSON must be an array (or an object with an 'items' array).");
            foreach (var o in arr.EnumerateArray())
                rows.Add(o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString(), StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            var table = Csv(content);
            if (table.Count < 2) throw new FormatException("The file needs a header row and at least one data row.");
            var head = table[0].Select(h => h.Trim()).ToList();
            foreach (var line in table.Skip(1))
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < head.Count && i < line.Count; i++) d[head[i]] = line[i];
                rows.Add(d);
            }
        }
        var result = new List<CostRow>(); var problems = new List<string>(); var n = 0;
        foreach (var r in rows)
        {
            n++;
            if (!r.TryGetValue(skuCol, out var sku) || string.IsNullOrWhiteSpace(sku)) { problems.Add($"row {n}: missing '{skuCol}'"); continue; }
            if (!r.TryGetValue(costCol, out var cs) || !decimal.TryParse(cs.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) || cost < 0)
            { problems.Add($"row {n} ({sku}): bad '{costCol}' value '{cs}'"); continue; }
            result.Add(new CostRow(sku.Trim(), r.TryGetValue(descCol, out var ds) && !string.IsNullOrWhiteSpace(ds) ? ds.Trim() : null, cost));
        }
        if (result.Count == 0) throw new FormatException("No usable rows. " + string.Join("; ", problems.Take(5)));
        if (problems.Count > 0 && problems.Count > result.Count / 2) throw new FormatException($"Too many bad rows ({problems.Count} of {n}); nothing imported. " + string.Join("; ", problems.Take(5)));
        return result;
    }

    static List<List<string>> Csv(string s)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var f = new StringBuilder(); var q = false;
        s = s.TrimStart('﻿');
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (q) { if (ch == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { f.Append('"'); i++; } else q = false; } else f.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                row.Add(f.ToString()); f.Clear();
                if (row.Any(x => x.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else f.Append(ch);
        }
        row.Add(f.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row);
        return rows;
    }

    // ---- feed fetching (SSRF-guarded) ---------------------------------------------------------------------------

    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ok = addrs.FirstOrDefault(a => !IsInternal(a)) ?? throw new HttpRequestException("Feed host resolves to a private or loopback address; refused.");
            var s = new Socket(ok.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await s.ConnectAsync(new IPEndPoint(ok, ctx.DnsEndPoint.Port), ct); return new NetworkStream(s, true); } catch { s.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 10 * 1024 * 1024 };

    static bool IsInternal(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return a.Equals(IPAddress.IPv6None) || a.Equals(IPAddress.IPv6Any) || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = a.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168) || b[0] >= 224;
    }

    public static async Task<string> FetchAsync(string url, string? authHeader, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw new FormatException("Feed URL must be a valid https:// address.");
        using var req = new HttpRequestMessage(HttpMethod.Get, u);
        if (!string.IsNullOrWhiteSpace(authHeader)) req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Feed returned HTTP {(int)res.StatusCode}.");
        return await res.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Fetch one supplier's feed, ingest it and record the outcome on the supplier row.</summary>
    public static async Task<IngestResult> RunFeedAsync(NpgsqlDataSource db, int supplierId, CancellationToken ct)
    {
        string? url, auth, fmt; JsonElement map;
        await using (var c = db.CreateCommand("SELECT feed_url, feed_auth_header, feed_format, column_map::text FROM supplier WHERE id = $1"))
        {
            c.Parameters.AddWithValue(supplierId);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new KeyNotFoundException();
            url = r.IsDBNull(0) ? null : r.GetString(0); auth = r.IsDBNull(1) ? null : r.GetString(1); fmt = r.GetString(2);
            map = JsonDocument.Parse(r.GetString(3)).RootElement.Clone();
        }
        try
        {
            if (string.IsNullOrWhiteSpace(url)) throw new FormatException("No feed URL set.");
            var rows = Parse(await FetchAsync(url, auth, ct), fmt, map);
            var res = await IngestAsync(db, supplierId, rows, "feed", ct);
            await Mark(db, supplierId, "ok", $"{rows.Count} items: {res.Added} new, {res.Changed} changed; {res.PriceChanges} price changes proposed.", ct);
            return res;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Mark(db, supplierId, "error", e.Message.Length > 300 ? e.Message[..300] : e.Message, ct);
            throw;
        }
    }

    static async Task Mark(NpgsqlDataSource db, int id, string status, string msg, CancellationToken ct)
    {
        await using var c = db.CreateCommand("UPDATE supplier SET last_run_at = now(), last_status = $2, last_message = $3 WHERE id = $1");
        c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(status); c.Parameters.AddWithValue(msg);
        await c.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Wakes every 5 minutes and runs feed suppliers whose refresh interval has elapsed. Failures are recorded, never fatal.</summary>
public sealed class PricingScheduler(NpgsqlDataSource db, ILogger<PricingScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                var due = new List<int>();
                await using (var c = db.CreateCommand("""
                    SELECT id FROM supplier WHERE active AND source_type = 'feed' AND feed_url IS NOT NULL AND refresh_hours IS NOT NULL
                      AND (last_run_at IS NULL OR last_run_at + refresh_hours * interval '1 hour' <= now())
                    """))
                {
                    await using var r = await c.ExecuteReaderAsync(stop);
                    while (await r.ReadAsync(stop)) due.Add(r.GetInt32(0));
                }
                foreach (var id in due)
                {
                    try { await PricingService.RunFeedAsync(db, id, stop); log.LogInformation("Supplier {Id} feed processed.", id); }
                    catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Supplier {Id} feed failed: {Msg}", id, e.Message); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Pricing scheduler pass failed."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
