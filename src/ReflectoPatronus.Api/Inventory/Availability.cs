using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Pricing;

namespace ReflectoPatronus.Api.Inventory;

public record AvailBody(string? Mode, string? Url, string? AuthHeader, bool? ClearAuth, string? Format, JsonElement? Map, int? RefreshHours, string? Sku = null);

/// <summary>
/// What a supplier says it has in stock, read from its API (JSON or CSV) or from a web page. It only records the answer (supplier_stock);
/// it never changes our own counts or prices. If the URL contains {sku}, one request is made per tracked item; otherwise one request
/// returns a list and the rows are matched to supplier items by SKU. Fetching reuses the guarded client of the cost feeds:
/// https only, no private or loopback addresses, a size limit, and redirects followed only to other https addresses.
/// </summary>
public static class Availability
{
    public sealed record Config(string Mode, string? Url, string? Auth, string Format, JsonElement Map, int? RefreshHours)
    {
        public bool PerSku => Url?.Contains("{sku}", StringComparison.OrdinalIgnoreCase) == true;
    }
    public sealed record Found(string? Sku, decimal? Qty, string Status, string? Note);

    const int MaxItemsPerRun = 500;
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex DefaultIn = new(@"\b(in[\s_-]*stock|available|yes|true|ready)\b", RegexOptions.IgnoreCase, RegexTimeout);
    static readonly Regex DefaultOut = new(@"\b(out[\s_-]*of[\s_-]*stock|unavailable|sold[\s_-]*out|back[\s_-]*order(ed)?|no|false|none)\b", RegexOptions.IgnoreCase, RegexTimeout);

    public static void Map(RouteGroupBuilder h)
    {
        h.MapGet("/suppliers/{id:int}/availability", Get);
        h.MapPut("/suppliers/{id:int}/availability", Save);
        h.MapPost("/suppliers/{id:int}/availability/test", Test);
        h.MapPost("/suppliers/{id:int}/availability/poll", PollSupplier);
        h.MapPost("/items/{id:int}/poll", PollItem);
    }
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---- configuration ------------------------------------------------------------------------------------------------

    static async Task<(Config? Cfg, string? Name, object? Status)> Load(NpgsqlDataSource db, int id, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT name, avail_mode, avail_url, avail_auth_header, avail_format, avail_map::text, avail_refresh_hours, avail_last_at, avail_status, avail_message,
                   (SELECT count(*) FROM supplier_stock ss JOIN supplier_item si ON si.id = ss.supplier_item_id WHERE si.supplier_id = supplier.id)
            FROM supplier WHERE id = $1
            """);
        c.Parameters.AddWithValue(id);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null, null);
        var cfg = new Config(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
            JsonDocument.Parse(r.GetString(5)).RootElement.Clone(), r.IsDBNull(6) ? null : r.GetInt32(6));
        return (cfg, r.GetString(0), new { lastAt = r.IsDBNull(7) ? null : (DateTime?)r.GetDateTime(7), lastStatus = r.IsDBNull(8) ? null : r.GetString(8), lastMessage = r.IsDBNull(9) ? null : r.GetString(9), known = r.GetInt64(10) });
    }

    static async Task<IResult> Get(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        var (cfg, _, st) = await Load(db, id, ct);
        if (cfg is null) return Results.NotFound();
        return Results.Ok(new { mode = cfg.Mode, url = cfg.Url, hasAuthHeader = cfg.Auth is not null, format = cfg.Format, map = cfg.Map, refreshHours = cfg.RefreshHours, perSku = cfg.PerSku, status = st });
    }

    /// <summary>Validates a posted configuration. A blank auth header keeps the stored one (write-only, like the cost feeds).</summary>
    static string? Check(Config c)
    {
        if (c.Mode == "none") return null;
        if (c.Mode is not ("api" or "scrape")) return "Source must be none, api or scrape.";
        if (c.Mode == "scrape" && c.Format != "html") return "Reading a web page needs the html format.";
        if (c.Mode == "api" && c.Format is not ("json" or "csv")) return "An API gives json or csv.";
        if (!Uri.TryCreate(c.Url?.Replace("{sku}", "x", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return "The address must be a valid https:// URL.";
        if (c.RefreshHours is { } h && (h < 1 || h > 720)) return "Check every 1 to 720 hours, or leave blank to check only when asked.";
        string? M(string k) => c.Map.ValueKind == JsonValueKind.Object && c.Map.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        foreach (var k in new[] { "qtyPattern", "inPattern", "outPattern", "rowPattern" })
            if (M(k) is { } p) { try { _ = new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout); } catch (ArgumentException) { return $"The pattern \"{k}\" is not a valid regular expression."; } }
        if (c.Format == "csv" && c.PerSku) return "A CSV list is fetched once: remove {sku} from the address.";
        if (c.Format == "html")
        {
            if (c.PerSku && M("qtyPattern") is null && M("inPattern") is null && M("outPattern") is null) return "Say what to look for on the page: a quantity pattern, or an in-stock / out-of-stock pattern.";
            if (!c.PerSku)
            {
                var row = M("rowPattern");
                if (row is null || !row.Contains("?<sku>") || !(row.Contains("?<qty>") || row.Contains("?<status>"))) return "A list page needs a row pattern with the named groups (?<sku>…) and (?<qty>…).";
            }
        }
        if (c.Format == "json" && c.PerSku && M("qty") is null && M("status") is null) return "Name the field that holds the quantity (for example data.available).";
        return null;
    }

    static Config FromBody(AvailBody b, string? storedAuth)
        => new(b.Mode ?? "none", N(b.Url), N(b.AuthHeader) ?? (b.ClearAuth == true ? null : storedAuth), b.Format ?? "json", b.Map ?? JsonDocument.Parse("{}").RootElement.Clone(), b.RefreshHours);

    static async Task<IResult> Save(int id, AvailBody b, NpgsqlDataSource db, CancellationToken ct)
    {
        var (old, _, _) = await Load(db, id, ct);
        if (old is null) return Results.NotFound();
        var cfg = FromBody(b, old.Auth);
        if (Check(cfg) is { } bad) return Bad(bad);
        var off = cfg.Mode == "none";
        await using var c = db.CreateCommand("""
            UPDATE supplier SET avail_mode = $2, avail_url = $3, avail_auth_header = $4, avail_format = $5, avail_map = $6::jsonb, avail_refresh_hours = $7 WHERE id = $1
            """);
        c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(cfg.Mode);
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)(off ? null : cfg.Url) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)(off ? null : cfg.Auth) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        c.Parameters.AddWithValue(cfg.Format); c.Parameters.AddWithValue(cfg.Map.GetRawText());
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)(off ? null : cfg.RefreshHours) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        await c.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }

    // ---- fetching -----------------------------------------------------------------------------------------------------

    static async Task<string> Fetch(string url, string? auth, string format, CancellationToken ct)
    {
        var u = new Uri(url); var origin = u.Host;
        for (var hop = 0; hop < 4; hop++)
        {
            if (u.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("Only https:// addresses are allowed.");
            using var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.UserAgent.ParseAdd("ReflectoPatronus-stock-check");   // generic client name; nothing about the shop or its users is sent
            req.Headers.Accept.ParseAdd(format == "json" ? "application/json" : format == "csv" ? "text/csv, text/plain" : "text/html");
            if (!string.IsNullOrWhiteSpace(auth) && string.Equals(u.Host, origin, StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation("Authorization", auth);
            using var res = await PricingService.Http.SendAsync(req, ct);
            if ((int)res.StatusCode is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { } loc) { u = loc.IsAbsoluteUri ? loc : new Uri(u, loc); continue; }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"The supplier answered HTTP {(int)res.StatusCode}.");
            return await res.Content.ReadAsStringAsync(ct);
        }
        throw new HttpRequestException("Too many redirects.");
    }

    // ---- reading the answer -------------------------------------------------------------------------------------------

    static string? MapStr(JsonElement map, string key, string? dflt = null)
        => map.ValueKind == JsonValueKind.Object && map.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : dflt;

    /// <summary>The first number in a piece of text: "1,250", "100+", "12 sheets" all work. Null when there is none.</summary>
    public static decimal? ParseQty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"-?\d[\d,]*(\.\d+)?", RegexOptions.None, RegexTimeout);
        return m.Success && decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? Math.Max(d, 0) : null;
    }

    static Found Make(string? sku, decimal? qty, string? statusText, string? note, Regex? inRx = null, Regex? outRx = null)
    {
        if (qty is { } q) return new(sku, q, q > 0 ? "in_stock" : "out", note);
        if (statusText is { Length: > 0 })
        {
            var o = (outRx ?? DefaultOut).IsMatch(statusText); var i = (inRx ?? DefaultIn).IsMatch(statusText);
            if (o && !i) return new(sku, null, "out", note ?? statusText); if (i && !o) return new(sku, null, "in_stock", note ?? statusText);
        }
        return new(sku, null, "unknown", note ?? "The supplier's answer had no quantity or stock status.");
    }

    static JsonElement? Path(JsonElement e, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(part, out var v)) e = v;
            else if (e.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i >= 0 && i < e.GetArrayLength()) e = e[i];
            else return null;
        }
        return e;
    }
    static string? Text(JsonElement? e) => e is null ? null : e.Value.ValueKind switch
    {
        JsonValueKind.String => e.Value.GetString(), JsonValueKind.Number => e.Value.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null,
    };
    static Regex? Rx(JsonElement map, string key) => MapStr(map, key) is { } p ? new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout) : null;

    /// <summary>One answer for one SKU (the URL had {sku}).</summary>
    public static Found ReadOne(string body, Config c)
    {
        var m = c.Map; Regex? inRx = Rx(m, "inPattern"), outRx = Rx(m, "outPattern");
        if (c.Format == "json")
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var q = MapStr(m, "qty") is { } qp ? ParseQty(Text(Path(root, qp))) : null;
            var st = MapStr(m, "status") is { } sp ? Text(Path(root, sp)) : null;
            return Make(null, q, st, null, inRx, outRx);
        }
        // html
        decimal? qty = null;
        if (Rx(m, "qtyPattern") is { } qr && qr.Match(body) is { Success: true } mm)
            qty = ParseQty(mm.Groups["qty"].Success ? mm.Groups["qty"].Value : mm.Groups.Count > 1 ? mm.Groups[1].Value : mm.Value);
        if (qty is null)
        {
            if (outRx is not null && outRx.IsMatch(body)) return new(null, null, "out", "Matched the out-of-stock pattern.");
            if (inRx is not null && inRx.IsMatch(body)) return new(null, null, "in_stock", "Matched the in-stock pattern.");
            return new(null, null, "unknown", "Nothing on the page matched the patterns.");
        }
        return Make(null, qty, null, null);
    }

    /// <summary>A list: one answer per SKU (the URL had no {sku}).</summary>
    public static List<Found> ReadList(string body, Config c)
    {
        var m = c.Map; var list = new List<Found>();
        Regex? inRx = Rx(m, "inPattern"), outRx = Rx(m, "outPattern");
        string skuK = MapStr(m, "sku", "sku")!, qtyK = MapStr(m, "qty", "qty")!, stK = MapStr(m, "status", "status")!;
        if (c.Format == "json")
        {
            using var doc = JsonDocument.Parse(body);
            var rows = MapStr(m, "rows") is { } rp ? Path(doc.RootElement, rp) : doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("items", out var it) ? it : null;
            if (rows is not { ValueKind: JsonValueKind.Array } arr) throw new FormatException("The answer must be a list (or an object with an 'items' list; set \"rows\" to name another).");
            foreach (var o in arr.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                var sku = Text(Path(o, skuK)); if (string.IsNullOrWhiteSpace(sku)) continue;
                list.Add(Make(sku.Trim(), ParseQty(Text(Path(o, qtyK))), Text(Path(o, stK)), null, inRx, outRx));
            }
        }
        else if (c.Format == "csv")
        {
            var t = PricingService.Csv(body);
            if (t.Count < 2) throw new FormatException("The CSV needs a header row and at least one data row.");
            var head = t[0].Select(h => h.Trim()).ToList();
            int si = head.FindIndex(h => h.Equals(skuK, StringComparison.OrdinalIgnoreCase)), qi = head.FindIndex(h => h.Equals(qtyK, StringComparison.OrdinalIgnoreCase)), ti = head.FindIndex(h => h.Equals(stK, StringComparison.OrdinalIgnoreCase));
            if (si < 0 || (qi < 0 && ti < 0)) throw new FormatException($"The CSV has no '{skuK}' column and '{qtyK}' (or '{stK}') column.");
            foreach (var line in t.Skip(1))
            {
                if (si >= line.Count || string.IsNullOrWhiteSpace(line[si])) continue;
                list.Add(Make(line[si].Trim(), qi >= 0 && qi < line.Count ? ParseQty(line[qi]) : null, ti >= 0 && ti < line.Count ? line[ti] : null, null, inRx, outRx));
            }
        }
        else
        {
            var row = Rx(m, "rowPattern") ?? throw new FormatException("A list page needs a row pattern.");
            foreach (Match mm in row.Matches(body))
            {
                var sku = mm.Groups["sku"].Success ? mm.Groups["sku"].Value.Trim() : null; if (string.IsNullOrWhiteSpace(sku)) continue;
                list.Add(Make(sku, mm.Groups["qty"].Success ? ParseQty(WebUtility.HtmlDecode(mm.Groups["qty"].Value)) : null, mm.Groups["status"].Success ? WebUtility.HtmlDecode(mm.Groups["status"].Value) : null, null, inRx, outRx));
                if (list.Count >= 20000) break;
            }
        }
        return list;
    }

    // ---- polling ------------------------------------------------------------------------------------------------------

    public sealed record PollResult(int Checked, int InStock, int Out, int Unknown, string Message);

    sealed record Target(int SupplierItemId, string Sku);

    static async Task Store(NpgsqlDataSource db, int supplierItemId, Found f, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            INSERT INTO supplier_stock (supplier_item_id, qty, status, note, checked_at) VALUES ($1, $2, $3, $4, now())
            ON CONFLICT (supplier_item_id) DO UPDATE SET qty = EXCLUDED.qty, status = EXCLUDED.status, note = EXCLUDED.note, checked_at = now()
            """);
        c.Parameters.AddWithValue(supplierItemId);
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Qty ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        c.Parameters.AddWithValue(f.Status);
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)(f.Note is { Length: > 300 } ? f.Note[..300] : f.Note) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Polls one supplier (all of its tracked items, or just one) and records the outcome on the supplier row.</summary>
    public static async Task<PollResult> Poll(NpgsqlDataSource db, int supplierId, int? onlySupplierItemId, CancellationToken ct)
    {
        var (cfg, _, _) = await Load(db, supplierId, ct);
        if (cfg is null) throw new KeyNotFoundException();
        try
        {
            if (cfg.Mode == "none" || string.IsNullOrWhiteSpace(cfg.Url)) throw new FormatException("This supplier has no stock source set up.");
            var targets = new List<Target>();
            await using (var q = db.CreateCommand("""
                SELECT si.id, si.sku FROM supplier_item si WHERE si.supplier_id = $1 AND ($2::int IS NULL OR si.id = $2)
                  AND ($2::int IS NOT NULL OR EXISTS (SELECT 1 FROM inventory_item i WHERE i.supplier_item_id = si.id AND i.active))
                ORDER BY si.id LIMIT $3
                """))
            {
                q.Parameters.AddWithValue(supplierId); q.Parameters.Add(new NpgsqlParameter { Value = (object?)onlySupplierItemId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer }); q.Parameters.AddWithValue(MaxItemsPerRun);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) targets.Add(new(r.GetInt32(0), r.GetString(1)));
            }
            if (targets.Count == 0) throw new FormatException("No tracked items for this supplier yet: start counting some items first.");

            int inS = 0, outS = 0, unk = 0, failures = 0; string? firstError = null;
            void Tally(Found f) { if (f.Status == "in_stock") inS++; else if (f.Status == "out") outS++; else unk++; }
            if (cfg.PerSku)
            {
                var consecutive = 0;
                foreach (var t in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    Found f;
                    try
                    {
                        var body = await Fetch(cfg.Url.Replace("{sku}", Uri.EscapeDataString(t.Sku), StringComparison.OrdinalIgnoreCase), cfg.Auth, cfg.Format, ct);
                        f = ReadOne(body, cfg); consecutive = 0;
                    }
                    catch (Exception e) when (e is HttpRequestException or JsonException or FormatException or RegexMatchTimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        f = new(null, null, "unknown", Short(e)); failures++; consecutive++; firstError ??= Short(e);
                        if (consecutive >= 5) { Tally(f); await Store(db, t.SupplierItemId, f, ct); break; }
                    }
                    Tally(f); await Store(db, t.SupplierItemId, f, ct);
                    if (targets.Count > 1) await Task.Delay(300, ct);   // be polite to the supplier's server
                }
            }
            else
            {
                var found = ReadList(await Fetch(cfg.Url, cfg.Auth, cfg.Format, ct), cfg);
                var bySku = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in found) bySku[f.Sku!] = f;
                foreach (var t in targets)
                {
                    var f = bySku.TryGetValue(t.Sku.Trim(), out var hit) ? hit : new Found(t.Sku, null, "unknown", "Not in the supplier's list.");
                    Tally(f); await Store(db, t.SupplierItemId, f, ct);
                }
            }
            var done = inS + outS + unk;
            var msg = $"{done} item{(done == 1 ? "" : "s")} checked: {inS} in stock, {outS} out, {unk} unknown." + (failures > 0 ? $" {failures} failed ({firstError})." : "");
            await Mark(db, supplierId, failures == 0 ? (unk == done ? "partial" : "ok") : failures == done ? "error" : "partial", msg, ct);
            return new(done, inS, outS, unk, msg);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Mark(db, supplierId, "error", Short(e), ct);
            throw;
        }
    }

    static string Short(Exception e)
    {
        var m = e is JsonException ? "The answer is not valid JSON." : e is RegexMatchTimeoutException ? "A pattern took too long to match." : e.Message;
        return m.Length > 300 ? m[..300] : m;
    }

    static async Task Mark(NpgsqlDataSource db, int id, string status, string msg, CancellationToken ct)
    {
        await using var c = db.CreateCommand("UPDATE supplier SET avail_last_at = now(), avail_status = $2, avail_message = $3 WHERE id = $1");
        c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(status); c.Parameters.AddWithValue(msg);
        await c.ExecuteNonQueryAsync(ct);
    }

    static async Task<IResult> Run(Func<Task<PollResult>> act, CancellationToken ct)
    {
        try { return Results.Ok(await act()); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (FormatException e) { return Bad(e.Message); }
        catch (JsonException) { return Bad("The supplier's answer is not valid JSON."); }
        catch (HttpRequestException e) { return Bad("Stock request failed: " + e.Message); }
        catch (RegexMatchTimeoutException) { return Bad("A pattern took too long to match."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Bad("Stock request timed out."); }
    }

    static async Task<IResult> PollSupplier(int id, NpgsqlDataSource db, CancellationToken ct) => await Run(() => Poll(db, id, null, ct), ct);

    static async Task<IResult> PollItem(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT si.supplier_id, si.id FROM inventory_item i JOIN supplier_item si ON si.id = i.supplier_item_id WHERE i.id = $1");
        c.Parameters.AddWithValue(id);
        int sup, sitem;
        await using (var r = await c.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) return Results.NotFound(); sup = r.GetInt32(0); sitem = r.GetInt32(1); }
        return await Run(() => Poll(db, sup, sitem, ct), ct);
    }

    /// <summary>Tries a (possibly unsaved) setup without storing anything: returns what it read, so patterns can be tuned before saving.</summary>
    static async Task<IResult> Test(int id, AvailBody b, NpgsqlDataSource db, CancellationToken ct)
    {
        var (old, _, _) = await Load(db, id, ct);
        if (old is null) return Results.NotFound();
        var cfg = FromBody(b, old.Auth);
        if (cfg.Mode == "none") return Bad("Choose api or scrape first.");
        if (Check(cfg) is { } bad) return Bad(bad);
        try
        {
            if (cfg.PerSku)
            {
                var sku = N(b.Sku);
                if (sku is null)
                {
                    await using var q = db.CreateCommand("SELECT sku FROM supplier_item WHERE supplier_id = $1 ORDER BY id LIMIT 1"); q.Parameters.AddWithValue(id);
                    sku = await q.ExecuteScalarAsync(ct) as string;
                }
                if (sku is null) return Bad("Add an item for this supplier first, or give a SKU to try.");
                var body = await Fetch(cfg.Url!.Replace("{sku}", Uri.EscapeDataString(sku), StringComparison.OrdinalIgnoreCase), cfg.Auth, cfg.Format, ct);
                var f = ReadOne(body, cfg);
                return Results.Ok(new { sku, length = body.Length, found = new[] { f with { Sku = sku } }, snippet = Snippet(body) });
            }
            var all = await Fetch(cfg.Url!, cfg.Auth, cfg.Format, ct);
            var list = ReadList(all, cfg);
            return Results.Ok(new { sku = (string?)null, length = all.Length, total = list.Count, found = list.Take(20), snippet = Snippet(all) });
        }
        catch (FormatException e) { return Bad(e.Message); }
        catch (JsonException) { return Bad("The supplier's answer is not valid JSON."); }
        catch (HttpRequestException e) { return Bad("Request failed: " + e.Message); }
        catch (RegexMatchTimeoutException) { return Bad("A pattern took too long to match."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Bad("The request timed out."); }
    }
    static string Snippet(string body) => body.Length > 400 ? body[..400] : body;
}

/// <summary>Wakes every 10 minutes and polls suppliers whose "check every N hours" has elapsed (only while inventory is on). Failures are recorded, never fatal.</summary>
public sealed class AvailabilityScheduler(NpgsqlDataSource db, ILogger<AvailabilityScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try
            {
                if (!await Stock.IsOn(db, stop)) continue;
                var due = new List<int>();
                await using (var c = db.CreateCommand("""
                    SELECT id FROM supplier WHERE active AND avail_mode <> 'none' AND avail_url IS NOT NULL AND avail_refresh_hours IS NOT NULL
                      AND (avail_last_at IS NULL OR avail_last_at + avail_refresh_hours * interval '1 hour' <= now())
                    """))
                {
                    await using var r = await c.ExecuteReaderAsync(stop);
                    while (await r.ReadAsync(stop)) due.Add(r.GetInt32(0));
                }
                foreach (var id in due)
                {
                    try { var p = await Availability.Poll(db, id, null, stop); log.LogInformation("Supplier {Id} stock checked: {Msg}", id, p.Message); }
                    catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Supplier {Id} stock check failed: {Msg}", id, e.Message); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Stock availability pass failed."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
