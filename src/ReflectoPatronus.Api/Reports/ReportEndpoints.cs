using Npgsql;
using NpgsqlTypes;

namespace ReflectoPatronus.Api.Reports;

/// <summary>Type: text, money, date, int, number. Total = the screen adds a totals row for this column.</summary>
public record ReportColumn(string Key, string Label, string Type = "text", bool Total = false);
/// <summary>Params the screen asks for: range (from/to), asof, customer, doctype. Sql always receives $1 from, $2 to, $3 as-of, $4 customer id, $5 document type.</summary>
public record ReportDef(string Id, string Title, string Group, string[] Params, ReportColumn[] Columns, string Sql, string? Note = null);

/// <summary>
/// Read-only reports. Each is one SQL statement in the registry below with the same five parameters, so adding a report is adding a row.
/// Sales figures use invoices and credit memos that are not voided (credit memos are negative, so they subtract).
/// </summary>
public static class ReportEndpoints
{
    static ReportColumn C(string k, string l, string t = "text", bool total = false) => new(k, l, t, total);
    const string Sales = "FROM document d JOIN document_totals t ON t.document_id = d.id WHERE d.doc_type IN ('I', 'C') AND NOT d.voided AND d.doc_date BETWEEN $1::date AND $2::date";
    const string Tz = "coalesce((SELECT time_zone FROM company ORDER BY id LIMIT 1), 'America/Chicago')";
    static readonly string[] Rng = ["range"];
    static readonly string[] None = [];

    public static readonly ReportDef[] Defs =
    [
        new("sales-breakdown", "Sales Breakdown", "Sales", Rng, [C("month", "Month"), C("docs", "Documents", "int", true), C("glass", "Glass", "money", true), C("part", "Parts", "money", true), C("labor", "Labor", "money", true), C("other", "Other", "money", true), C("tax", "Tax", "money", true), C("total", "Total", "money", true)],
            $"SELECT to_char(date_trunc('month', d.doc_date), 'YYYY-MM'), count(*), sum(t.glass_total), sum(t.part_total), sum(t.labor_total), sum(t.custom_total), sum(t.tax_total), sum(t.total) {Sales} GROUP BY 1 ORDER BY 1",
            "Invoices less credit memos, by month."),
        new("sales-by-category", "Sales by Customer Category", "Sales", Rng, [C("category", "Category"), C("description", "Description"), C("docs", "Documents", "int", true), C("sales", "Sales (before tax)", "money", true), C("tax", "Tax", "money", true), C("total", "Total", "money", true)],
            $"SELECT coalesce(d.customer_category, '(none)'), cc.description, count(*), sum(t.lines_total), sum(t.tax_total), sum(t.total) {Sales.Replace("WHERE", "LEFT JOIN customer_category cc ON cc.code = d.customer_category WHERE")} GROUP BY 1, 2 ORDER BY 6 DESC"),
        new("sales-by-agent", "Sales by Agent", "Sales", Rng, [C("agent", "Salesperson"), C("docs", "Documents", "int", true), C("sales", "Sales (before tax)", "money", true), C("tax", "Tax", "money", true), C("total", "Total", "money", true)],
            $"SELECT coalesce(s.name, s.code, '(none)'), count(*), sum(t.lines_total), sum(t.tax_total), sum(t.total) {Sales.Replace("WHERE", "LEFT JOIN staff s ON s.id = d.staff_sales_id WHERE")} GROUP BY 1 ORDER BY 5 DESC"),
        new("sales-detail-by-agent", "Sales Detail by Agent", "Sales", Rng, [C("agent", "Salesperson"), C("date", "Date", "date"), C("number", "Document"), C("customer", "Customer"), C("sales", "Sales", "money", true), C("tax", "Tax", "money", true), C("total", "Total", "money", true)],
            $"SELECT coalesce(s.name, s.code, '(none)'), d.doc_date, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), trim(coalesce(d.cust_last_name, '') || ' ' || coalesce(d.cust_first_name, '')), t.lines_total, t.tax_total, t.total {Sales.Replace("WHERE", "LEFT JOIN staff s ON s.id = d.staff_sales_id WHERE")} ORDER BY 1, d.doc_date, d.id"),
        new("tax-breakdown", "Tax Breakdown", "Sales", Rng, [C("code", "Tax code"), C("percent", "Rate %", "number"), C("docs", "Documents", "int", true), C("tax", "Tax collected", "money", true)],
            "SELECT x.tax_code, max(x.percent), count(DISTINCT d.id), sum(x.amount) FROM document d JOIN document_tax x ON x.document_id = d.id WHERE d.doc_type IN ('I', 'C') AND NOT d.voided AND d.doc_date BETWEEN $1::date AND $2::date GROUP BY 1 ORDER BY 1"),
        new("aged-open-invoices", "Aged Open Invoices", "Receivables", ["asof"], [C("number", "Account"), C("customer", "Customer"), C("current", "0-30 days", "money", true), C("d30", "31-60", "money", true), C("d60", "61-90", "money", true), C("d90", "Over 90", "money", true), C("total", "Balance", "money", true)],
            """
            SELECT c.number, trim(c.last_name || ' ' || coalesce(c.first_name, '')),
                   sum(b.bal) FILTER (WHERE $3::date - d.doc_date <= 30), sum(b.bal) FILTER (WHERE $3::date - d.doc_date BETWEEN 31 AND 60),
                   sum(b.bal) FILTER (WHERE $3::date - d.doc_date BETWEEN 61 AND 90), sum(b.bal) FILTER (WHERE $3::date - d.doc_date > 90), sum(b.bal)
            FROM (SELECT customer_id, document_id, sum(amount) AS bal FROM ar_entry WHERE document_id IS NOT NULL GROUP BY 1, 2 HAVING sum(amount) <> 0) b
            JOIN document d ON d.id = b.document_id JOIN customer c ON c.id = b.customer_id
            GROUP BY c.id, c.number, c.last_name, c.first_name ORDER BY sum(b.bal) DESC
            """, "Age is counted from the document date to the as-of date. Credits reduce the bucket they sit in."),
        new("open-items", "Statements (open items)", "Receivables", None, [C("customer", "Customer"), C("number", "Document"), C("type", "Type"), C("date", "Date", "date"), C("po", "PO"), C("amount", "Amount", "money"), C("balance", "Balance", "money", true)],
            """
            SELECT trim(c.last_name || ' ' || coalesce(c.first_name, '')), doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), dt.name, d.doc_date, d.po_number, t.total, b.bal
            FROM (SELECT customer_id, document_id, sum(amount) AS bal FROM ar_entry WHERE document_id IS NOT NULL GROUP BY 1, 2 HAVING sum(amount) <> 0) b
            JOIN document d ON d.id = b.document_id JOIN document_totals t ON t.document_id = d.id JOIN doc_type dt ON dt.code = d.doc_type JOIN customer c ON c.id = b.customer_id
            ORDER BY lower(c.last_name), c.id, d.doc_date, d.id
            """, "Everything each customer owes. Print one customer's statement from the Receivables window."),
        new("list-payments", "List Payments", "Receivables", Rng, [C("date", "Date", "date"), C("customer", "Customer"), C("type", "Type"), C("category", "Paid by"), C("ref", "Reference"), C("user", "Entered by"), C("amount", "Amount", "money", true), C("status", "")],
            $"SELECT (p.paid_at AT TIME ZONE {Tz})::date, trim(c.last_name || ' ' || coalesce(c.first_name, '')), CASE p.type WHEN 'D' THEN 'Deposit' ELSE 'Payment' END, pc.description, p.description, u.code, CASE WHEN p.voided THEN 0 ELSE p.amount END, CASE WHEN p.voided THEN 'VOID' ELSE '' END FROM payment p JOIN customer c ON c.id = p.customer_id LEFT JOIN pay_category pc ON pc.code = p.category LEFT JOIN app_user u ON u.id = p.created_by WHERE (p.paid_at AT TIME ZONE {Tz})::date BETWEEN $1::date AND $2::date ORDER BY p.paid_at, p.id"),
        new("payment-breakdown", "Payment Breakdown", "Receivables", Rng, [C("type", "Type"), C("category", "Paid by"), C("count", "Payments", "int", true), C("amount", "Amount", "money", true)],
            $"SELECT CASE p.type WHEN 'D' THEN 'Deposit' ELSE 'Payment' END, coalesce(pc.description, '(not recorded)'), count(*), sum(p.amount) FROM payment p LEFT JOIN pay_category pc ON pc.code = p.category WHERE NOT p.voided AND (p.paid_at AT TIME ZONE {Tz})::date BETWEEN $1::date AND $2::date GROUP BY 1, 2 ORDER BY 1, 2"),
        new("payments-by-user", "Payments by User", "Receivables", Rng, [C("user", "Entered by"), C("count", "Payments", "int", true), C("amount", "Amount", "money", true)],
            $"SELECT coalesce(u.code, '(unknown)'), count(*), sum(p.amount) FROM payment p LEFT JOIN app_user u ON u.id = p.created_by WHERE NOT p.voided AND (p.paid_at AT TIME ZONE {Tz})::date BETWEEN $1::date AND $2::date GROUP BY 1 ORDER BY 3 DESC"),
        new("customer-list", "Customer List", "Accounts", None, [C("number", "Account"), C("name", "Name"), C("address", "Address"), C("phone", "Phone"), C("category", "Category"), C("balance", "Balance", "money", true)],
            "SELECT c.number, trim(c.last_name || ' ' || coalesce(c.first_name, '')), concat_ws(', ', nullif(c.address1, ''), nullif(c.city, ''), nullif(c.state, '')), (SELECT number FROM customer_phone WHERE customer_id = c.id ORDER BY slot LIMIT 1), c.category, coalesce(b.balance, 0) FROM customer c LEFT JOIN customer_balance b ON b.customer_id = c.id ORDER BY lower(c.last_name), lower(coalesce(c.first_name, ''))"),
        new("customer-categories", "List Customer Categories", "Accounts", None, [C("code", "Code"), C("description", "Description"), C("customers", "Customers", "int", true)],
            "SELECT cc.code, cc.description, count(c.id) FROM customer_category cc LEFT JOIN customer c ON c.category = cc.code GROUP BY 1, 2 ORDER BY 1"),
        new("special-pricing-list", "Customer Special Pricing List", "Accounts", None, [C("customer", "Customer"), C("part", "Part"), C("thick", "Thickness"), C("category", "Price category"), C("price", "Price", "money")],
            "SELECT trim(c.last_name || ' ' || coalesce(c.first_name, '')), p.code, CASE WHEN s.thick_den = 0 THEN '' ELSE s.thick_num || '/' || s.thick_den END, s.price_category, s.price FROM customer_part_price s JOIN customer c ON c.id = s.customer_id JOIN part p ON p.id = s.part_id ORDER BY lower(c.last_name), p.code"),
        new("customer-history", "Customer History", "Accounts", ["customer"], [C("date", "Date", "date"), C("type", "Type"), C("number", "Document"), C("po", "PO"), C("total", "Total", "money", true), C("balance", "Balance", "money", true), C("status", "")],
            "SELECT d.doc_date, dt.name, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), d.po_number, CASE WHEN d.voided THEN 0 ELSE t.total END, coalesce((SELECT sum(amount) FROM ar_entry WHERE document_id = d.id), 0), CASE WHEN d.voided THEN 'VOID' ELSE '' END FROM document d JOIN document_totals t ON t.document_id = d.id JOIN doc_type dt ON dt.code = d.doc_type WHERE d.customer_id = $4 ORDER BY d.doc_date DESC, d.id DESC"),
        new("list-salesmen", "List Salesman", "Staff", None, [C("code", "Code"), C("name", "Name"), C("phone", "Phone"), C("status", "Status")],
            "SELECT code, name, phone, CASE WHEN deleted THEN 'Inactive' ELSE 'Active' END FROM staff WHERE is_salesman ORDER BY lower(code)"),
        new("list-installers", "List Installers", "Staff", None, [C("code", "Code"), C("name", "Name"), C("phone", "Phone"), C("rate", "Rate", "money"), C("status", "Status")],
            "SELECT code, name, phone, rate, CASE WHEN deleted THEN 'Inactive' ELSE 'Active' END FROM staff WHERE is_installer ORDER BY lower(code)"),
        new("installer-totals", "Installer Totals", "Staff", Rng, [C("code", "Installer"), C("jobs", "Jobs", "int", true), C("hours", "Hours", "number", true), C("amount", "Amount", "money", true)],
            "SELECT coalesce(s.name, i.installer_code), count(DISTINCT d.id), sum(i.hours), sum(i.amount) FROM document_installer i JOIN document d ON d.id = i.document_id LEFT JOIN staff s ON s.id = i.staff_id WHERE NOT d.voided AND d.doc_date BETWEEN $1::date AND $2::date GROUP BY 1 ORDER BY 4 DESC NULLS LAST"),
        new("work-order-status", "Work Order Status", "Staff", None, [C("number", "Work order"), C("date", "Date", "date"), C("customer", "Customer"), C("po", "PO"), C("age", "Days open", "int"), C("total", "Total", "money", true), C("deposit", "Deposits", "money", true)],
            $"SELECT doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), d.doc_date, trim(coalesce(d.cust_last_name, '') || ' ' || coalesce(d.cust_first_name, '')), d.po_number, (now() AT TIME ZONE {Tz})::date - d.doc_date, t.total, coalesce(-(SELECT sum(amount) FROM ar_entry WHERE document_id = d.id AND purpose IN (4, 5)), 0) FROM document d JOIN document_totals t ON t.document_id = d.id WHERE d.doc_type = 'W' AND NOT d.voided ORDER BY d.doc_date, d.id"),
        new("list-documents", "List Quotes, WO's, Invoices", "File Listing", ["range", "doctype"], [C("date", "Date", "date"), C("type", "Type"), C("number", "Document"), C("customer", "Customer"), C("po", "PO"), C("total", "Total", "money", true), C("status", "")],
            "SELECT d.doc_date, dt.name, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), trim(coalesce(d.cust_last_name, '') || ' ' || coalesce(d.cust_first_name, '')), d.po_number, CASE WHEN d.voided THEN 0 ELSE t.total END, CASE WHEN d.voided THEN 'VOID' ELSE '' END FROM document d JOIN document_totals t ON t.document_id = d.id JOIN doc_type dt ON dt.code = d.doc_type WHERE d.doc_date BETWEEN $1::date AND $2::date AND ($5::text IS NULL OR d.doc_type = $5) ORDER BY d.doc_date, d.id"),
        new("list-price-categories", "List Price Categories", "File Listing", None, [C("code", "Code"), C("description", "Description"), C("customers", "Customers", "int", true)],
            "SELECT pc.code, pc.description, count(c.id) FROM price_category pc LEFT JOIN customer c ON c.price_category = pc.code GROUP BY 1, 2 ORDER BY 1"),
        new("list-glass-types", "List Flat Glass Types", "File Listing", None, [C("code", "Code"), C("description", "Description"), C("thick", "Thicknesses")],
            "SELECT p.code, p.description, (SELECT string_agg(CASE WHEN thick_den = 0 THEN '-' ELSE thick_num || '/' || thick_den END, ', ' ORDER BY thick_den, thick_num) FROM part_thickness WHERE part_id = p.id) FROM part p WHERE p.source = 'G' ORDER BY upper(p.code)"),
        new("list-parts", "List Flat Glass Parts", "File Listing", None, [C("code", "Code"), C("description", "Description"), C("uom", "Unit"), C("price", "Price (first category)", "money")],
            "SELECT p.code, p.description, u.short_name, (SELECT coalesce(nullif(x.price1, 0), nullif(x.price4, 0)) FROM part_price x JOIN part_thickness th ON th.id = x.part_thickness_id WHERE th.part_id = p.id ORDER BY x.price_category NULLS LAST, th.thick_den LIMIT 1) FROM part p LEFT JOIN unit_of_measure u ON u.id = p.uom_id WHERE p.source = 'P' ORDER BY upper(p.code)"),
        new("list-users", "List Users", "File Listing", None, [C("code", "Sign-in"), C("name", "Name"), C("role", "Role"), C("status", "Status")],
            "SELECT code, name, CASE WHEN is_admin THEN 'Administrator' ELSE 'User' END, CASE WHEN password_hash IS NULL THEN 'No sign-in' WHEN active THEN 'Active' ELSE 'Inactive' END FROM app_user WHERE password_hash IS NOT NULL OR active ORDER BY lower(code)"),
        new("list-companies", "List Companies", "File Listing", None, [C("name", "Name"), C("address", "Address"), C("phone", "Phone"), C("email", "E-mail")],
            "SELECT name, concat_ws(', ', nullif(address1, ''), nullif(city, ''), nullif(state, '')), phone, email FROM company ORDER BY id"),
    ];

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/reports");
        g.MapGet("", () => Results.Ok(Defs.Select(d => new { d.Id, d.Title, d.Group, d.Params, d.Columns, d.Note })));
        g.MapGet("/{id}", Run);
    }

    static async Task<IResult> Run(string id, DateOnly? from, DateOnly? to, DateOnly? asOf, int? customerId, string? type, NpgsqlDataSource db, CancellationToken ct)
    {
        var d = Defs.FirstOrDefault(x => x.Id == id); if (d is null) return Results.NotFound();
        if (d.Params.Contains("customer") && customerId is null) return Results.BadRequest(new { error = "Choose a customer." });
        var docType = type?.ToUpperInvariant() is "Q" or "W" or "I" or "C" ? type.ToUpperInvariant() : null;
        if (d.Params.Contains("range") && from is not null && to is not null && to < from) return Results.BadRequest(new { error = "The end date is before the start date." });
        await using var c = db.CreateCommand(d.Sql);
        c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = (object?)from?.ToDateTime(TimeOnly.MinValue) ?? new DateTime(1900, 1, 1) });
        c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = (object?)to?.ToDateTime(TimeOnly.MinValue) ?? new DateTime(2999, 12, 31) });
        c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = (object?)asOf?.ToDateTime(TimeOnly.MinValue) ?? DateTime.UtcNow.Date });
        c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)customerId ?? DBNull.Value });
        c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)docType ?? DBNull.Value });
        var rows = new List<object?[]>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct) && rows.Count < 20000)
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                var v = r.IsDBNull(i) ? null : r.GetValue(i);
                row[i] = v switch { DateOnly dt => dt.ToString("yyyy-MM-dd"), DateTime dt => dt.ToString("yyyy-MM-dd"), char ch => ch.ToString(), string s => s.Trim(), long l => l, _ => v };
            }
            rows.Add(row);
        }
        return Results.Ok(new { id = d.Id, title = d.Title, columns = d.Columns, rows, truncated = rows.Count >= 20000 });
    }
}
