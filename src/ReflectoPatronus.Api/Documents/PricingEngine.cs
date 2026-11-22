using System.Globalization;
using Npgsql;

namespace ReflectoPatronus.Api.Documents;

public class PricingException(string message) : Exception(message);

/// <summary>One line as the user typed it. Everything price-related is optional: the engine fills it from the catalog.</summary>
public record DraftLine(string? Source, int? Qty, int? PartId, short? ThickNum, short? ThickDen, decimal? WidthIn, decimal? HeightIn,
    decimal? Units, int? PriceLevel, decimal? Rate, decimal? UnitPrice, decimal? Amount, string? Description, bool? Taxable);

public record DraftDocument(int? CustomerId, string? PriceCategory, string? TaxGroup, decimal? DiscountPercent, IReadOnlyList<DraftLine>? Lines);

public record PricedLine(int Seq, string Source, int Qty, int? PartId, string? PartCode, string? Description, short? ThickNum, short? ThickDen,
    string? Thickness, decimal? WidthIn, decimal? HeightIn, string? Dims, string? Shape, decimal? Units, decimal? SqFt, decimal? Rate,
    decimal? ListPrice, decimal? UnitPrice, decimal Multiplier, decimal Amount, bool Taxable, bool SpecialPrice, bool AmountOverride,
    IReadOnlyList<string> Warnings);

public record CalcTax(string Code, decimal Percent, decimal Amount);
public record CalcResult(IReadOnlyList<PricedLine> Lines, IReadOnlyList<CalcTax> Taxes, decimal Subtotal, decimal Tax, decimal Total,
    string? PriceCategory, string? TaxGroup, IReadOnlyList<string> Warnings);

/// <summary>
/// Server-side pricing for quotes (so every client prices the same way). Industry-standard flat-glass rules:
///  - Glass is sold by the square foot. Billable area per piece = (width + waste) x (height + waste) / 144, where the
///    waste allowance per thickness comes from the loss table. Piece price = area x rate, never below the part's minimum.
///  - The rate comes from the part's price row for the document's price category (a customer-specific special price
///    wins; a price typed on the line wins over both). Price levels 1-4 are alternative columns; if the chosen level is
///    empty the first filled one is used.
///  - Parts are quantity x units x price (units = linear inches, feet, ... as the part defines; default 1), at least the minimum.
///  - Labor and custom lines are typed amounts (or quantity x rate).
///  - A document discount % reduces every line amount; tax is then charged per tax code of the tax group on the taxable
///    lines (labor only when the code says it taxes labor; "non-calculative" codes are skipped; exempt customers pay none).
///  - All money is rounded half away from zero to cents.
/// </summary>
public static class PricingEngine
{
    static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    static readonly string[] Sources = ["glass", "part", "labor", "custom"];

    /// <summary>Legacy size string: width then height, each iiii nnn ddd (whole inches, numerator, denominator).</summary>
    public static string EncodeDims(decimal w, decimal h) => Enc(w) + Enc(h);
    static string Enc(decimal v)
    {
        var sixteenths = (int)Math.Round(v * 16, MidpointRounding.AwayFromZero);
        int whole = sixteenths / 16, n = sixteenths % 16, d = 16;
        if (n == 0) return whole.ToString("0000") + "000000";
        while (n % 2 == 0) { n /= 2; d /= 2; }
        return whole.ToString("0000") + n.ToString("000") + d.ToString("000");
    }
    public static (decimal W, decimal H)? DecodeDims(string? s)
    {
        if (s is null || s.Length != 20 || !s.All(char.IsAsciiDigit)) return null;
        static decimal One(string x) { var d = int.Parse(x[7..10]); return int.Parse(x[..4]) + (d == 0 ? 0 : (decimal)int.Parse(x[4..7]) / d); }
        return (One(s[..10]), One(s[10..]));
    }

    public static async Task<CalcResult> CalculateAsync(NpgsqlDataSource db, DraftDocument doc, CancellationToken ct)
    {
        var warnings = new List<string>();
        bool exempt = false; string? custCat = null, custTax = null;
        if (doc.CustomerId is { } cid)
        {
            await using var c = db.CreateCommand("SELECT tax_exempt, price_category, tax_group FROM customer WHERE id = $1");
            c.Parameters.AddWithValue(cid);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new PricingException("Unknown customer.");
            exempt = r.GetBoolean(0); custCat = r.IsDBNull(1) ? null : r.GetString(1); custTax = r.IsDBNull(2) ? null : r.GetString(2);
        }
        string? coCat = null, coTax = null;
        await using (var c = db.CreateCommand("SELECT default_price_category, tax_group FROM company ORDER BY id LIMIT 1"))
        await using (var r = await c.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { coCat = r.IsDBNull(0) ? null : r.GetString(0); coTax = r.IsDBNull(1) ? null : r.GetString(1); }
        var category = Blank(doc.PriceCategory) ?? custCat ?? coCat;
        var taxGroup = Blank(doc.TaxGroup) ?? custTax ?? coTax;
        var disc = doc.DiscountPercent ?? 0m;
        if (disc is < 0 or > 100) throw new PricingException("Discount must be between 0 and 100 percent.");

        var lines = new List<PricedLine>();
        var input = doc.Lines ?? [];
        if (input.Count > 200) throw new PricingException("A document can have at most 200 lines.");
        for (var i = 0; i < input.Count; i++)
            lines.Add(await PriceLine(db, input[i], i + 1, doc.CustomerId, category, disc, ct));

        // ---- tax ----
        var taxes = new List<CalcTax>();
        if (!exempt && taxGroup is not null)
        {
            await using var c = db.CreateCommand("""
                SELECT tc.code, tc.percent, tc.taxes_labor, tc.non_calc FROM tax_group_code g
                JOIN tax_code tc ON tc.code = g.tax_code WHERE g.tax_group = $1 ORDER BY g.position
                """);
            c.Parameters.AddWithValue(taxGroup);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.GetBoolean(3)) continue;   // non-calculative: shown on the tax group, never added in
                var pct = r.GetDecimal(1); var labor = r.GetBoolean(2);
                var baseAmt = lines.Where(l => l.Taxable || (labor && l.Source == "labor")).Sum(l => l.Amount);
                taxes.Add(new CalcTax(r.GetString(0), pct, R2(baseAmt * pct / 100m)));
            }
        }
        var sub = lines.Sum(l => l.Amount); var tax = taxes.Sum(t => t.Amount);
        foreach (var w in lines.SelectMany(l => l.Warnings.Select(m => $"Line {l.Seq}: {m}"))) warnings.Add(w);
        return new CalcResult(lines, taxes, sub, tax, sub + tax, category, taxGroup, warnings);
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    record PriceRow(decimal?[] Level, decimal? Min, bool Taxable);

    static async Task<PricedLine> PriceLine(NpgsqlDataSource db, DraftLine l, int seq, int? customerId, string? category, decimal disc, CancellationToken ct)
    {
        string Err(string m) => $"Line {seq}: {m}";
        var source = (l.Source ?? "").ToLowerInvariant();
        if (!Sources.Contains(source)) throw new PricingException(Err("choose a line type (glass, part, labor or custom)."));
        var qty = l.Qty ?? 1;
        if (qty is < 1 or > 9999) throw new PricingException(Err("quantity must be 1 to 9999."));
        if (l.Amount is < -99_999_999 or > 99_999_999 || l.UnitPrice is < 0 or > 99_999_999 || l.Rate is < 0 or > 99_999_999)
            throw new PricingException(Err("a price or amount is out of range."));
        var warns = new List<string>();
        var desc = Blank(l.Description);
        decimal? unit = null, list = null, sqft = null, rate = null, units = l.Units; string? code = null; short? tn = null, td = null;
        decimal? w = null, h = null; string? dims = null, shape = null;
        decimal amount; var amountOverride = false; var special = false; var taxable = l.Taxable ?? false;

        if (source is "glass" or "part")
        {
            if (l.PartId is not { } pid) throw new PricingException(Err("choose a part."));
            string? pDesc; string pSource; int? uom;
            await using (var c = db.CreateCommand("SELECT code, description, source, uom_id FROM part WHERE id = $1"))
            {
                c.Parameters.AddWithValue(pid);
                await using var r = await c.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) throw new PricingException(Err("unknown part."));
                code = r.GetString(0); pDesc = r.IsDBNull(1) ? null : r.GetString(1); pSource = r.GetString(2).Trim(); uom = r.IsDBNull(3) ? null : r.GetInt16(3);
            }
            if ((source == "glass") != (pSource == "G")) throw new PricingException(Err($"{code} is a {(pSource == "G" ? "glass" : "part")} item."));
            desc ??= pDesc;

            // thickness: given, or the only one the part has
            int? ptId = null;
            await using (var c = db.CreateCommand("SELECT id, thick_num, thick_den FROM part_thickness WHERE part_id = $1 AND ($2::smallint IS NULL OR (thick_num = $2 AND thick_den = $3)) ORDER BY id"))
            {
                c.Parameters.AddWithValue(pid);
                c.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Smallint, (object?)l.ThickNum ?? DBNull.Value);
                c.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Smallint, (object?)l.ThickDen ?? DBNull.Value);
                var found = new List<(int, short, short)>();
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) found.Add((r.GetInt32(0), r.GetInt16(1), r.GetInt16(2)));
                if (found.Count == 0) throw new PricingException(Err($"{code} has no such thickness."));
                if (found.Count > 1) throw new PricingException(Err($"choose a thickness for {code}."));
                (ptId, tn, td) = (found[0].Item1, found[0].Item2, found[0].Item3);
            }

            // price row: the document's category first, then the all-categories row; the first with a non-zero price counts
            PriceRow? row = null;
            await using (var c = db.CreateCommand("""
                SELECT price1, price2, price3, price4, min_price, taxable FROM part_price
                WHERE part_thickness_id = $1 AND (price_category = $2 OR price_category IS NULL)
                ORDER BY (price_category IS NULL), id
                """))
            {
                c.Parameters.AddWithValue(ptId!.Value); c.Parameters.AddWithValue((object?)category ?? DBNull.Value);
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var lv = Enumerable.Range(0, 4).Select(k => r.IsDBNull(k) ? (decimal?)null : r.GetDecimal(k)).ToArray();
                    var cand = new PriceRow(lv, r.IsDBNull(4) ? null : r.GetDecimal(4), r.GetBoolean(5));
                    row ??= cand;
                    if (lv.Any(x => x > 0)) { row = cand; break; }
                }
            }
            if (l.Taxable is null) taxable = row?.Taxable ?? true;

            decimal? catalogRate = null;
            if (row is not null)
            {
                var lvl = Math.Clamp(l.PriceLevel ?? 1, 1, 4);
                catalogRate = row.Level[lvl - 1] is > 0 ? row.Level[lvl - 1] : row.Level.FirstOrDefault(x => x > 0);
            }
            if (l.Rate is null && l.UnitPrice is null && customerId is { } cu)
            {
                await using var c = db.CreateCommand("""
                    SELECT price FROM customer_part_price WHERE customer_id = $1 AND part_id = $2 AND thick_num = $3 AND thick_den = $4
                      AND (price_category = $5 OR price_category IS NULL) ORDER BY (price_category IS NULL), id LIMIT 1
                    """);
                c.Parameters.AddWithValue(cu); c.Parameters.AddWithValue(pid); c.Parameters.AddWithValue(tn!.Value); c.Parameters.AddWithValue(td!.Value);
                c.Parameters.AddWithValue((object?)category ?? DBNull.Value);
                var sp = await c.ExecuteScalarAsync(ct);
                if (sp is decimal spd) { catalogRate = spd; special = true; }
            }
            rate = l.Rate ?? catalogRate;
            if (l.Rate is not null) special = true;
            var min = row?.Min ?? 0m;
            var eachPricing = source == "part" || uom == 1 || uom is 2 or 3 or 4;   // glass sold per piece/unit instead of per sq ft

            if (source == "glass" && !eachPricing)
            {
                w = l.WidthIn; h = l.HeightIn;
                if (l.UnitPrice is null)
                {
                    if (w is not > 0 || h is not > 0) throw new PricingException(Err("enter the width and height."));
                    if (w > 1000 || h > 1000) throw new PricingException(Err("size is out of range."));
                    decimal loss = 0;
                    await using (var c = db.CreateCommand("SELECT loss_int, loss_num, loss_den FROM glass_loss WHERE thick_num = $1 AND thick_den = $2"))
                    {
                        c.Parameters.AddWithValue(tn!.Value); c.Parameters.AddWithValue(td!.Value);
                        await using var r = await c.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct)) loss = r.GetInt16(0) + (r.GetInt16(2) == 0 ? 0 : (decimal)r.GetInt16(1) / r.GetInt16(2));
                    }
                    sqft = Math.Round((w.Value + loss) * (h.Value + loss) / 144m, 4, MidpointRounding.AwayFromZero);
                }
                if (w > 0 && h > 0) { dims = EncodeDims(w.Value, h.Value); shape = "RECT"; }
                if (l.UnitPrice is { } fixedPiece) unit = fixedPiece;
                else if (rate is null or <= 0) { warns.Add("no price on file for this thickness and category; type a price."); unit = 0; }
                else unit = Math.Max(R2(sqft!.Value * rate.Value), R2(min));
            }
            else
            {
                units ??= 1;
                if (units is <= 0 or > 1_000_000) throw new PricingException(Err("units must be greater than zero."));
                if (source == "glass" && l.WidthIn > 0 && l.HeightIn > 0) { w = l.WidthIn; h = l.HeightIn; dims = EncodeDims(w.Value, h.Value); shape = "RECT"; }
                if (l.UnitPrice is { } fixedUnit) unit = fixedUnit;
                else if (rate is null or <= 0) { warns.Add("no price on file; type a price."); unit = 0; }
                else unit = Math.Max(R2(units.Value * rate.Value), R2(min));
            }
            list = unit;
            amount = R2(qty * unit!.Value);
        }
        else   // labor / custom: typed amounts
        {
            if (source == "labor") { desc ??= "Labor Charge"; code = "LABOR"; }
            var each = l.UnitPrice ?? l.Rate ?? 0m;
            amount = R2(qty * each); unit = each; list = each;
            if (l.Rate is not null) special = true;
        }

        if (l.Amount is { } typed) { amount = R2(typed); amountOverride = true; if (qty > 0) unit = R2(typed / qty); list = unit; }
        var discounted = disc == 0 ? amount : R2(amount * (1 - disc / 100m));
        return new PricedLine(seq, source, qty, l.PartId, code, desc, tn, td, tn is null ? null : $"{tn}/{td}", w, h, dims, shape, units, sqft, rate,
            list, unit, 1m, discounted, taxable, special, amountOverride, warns);
    }
}
