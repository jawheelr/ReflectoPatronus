using System.Globalization;
using System.Text;
using Npgsql;

namespace ReflectoPatronus.Api.Accounting;

/// <summary>
/// Files for accounting programs that cannot be pushed to. IIF is read by QuickBooks Desktop (File, Utilities, Import, IIF Files);
/// the Sage 50 (Peachtree) files are CSV with a header row and are brought in with File, Select Import/Export, where the columns are
/// matched to Sage's fields once and the layout is remembered.
/// </summary>
public static class FileExports
{
    public record Result(string FileName, string ContentType, string Text, int Count, IReadOnlyList<(string Kind, int RefId)> Items);

    static string D(DateOnly d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
    static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    static string T(string? s) => (s ?? "").Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ").Trim();
    static string Csv(string? s) { s ??= ""; return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
    static string Row(params string?[] cells) => string.Join(",", cells.Select(Csv));

    public static async Task<Result> Iif(NpgsqlDataSource db, AccountingConfig cfg, DateOnly from, DateOnly? to, bool includeSent, CancellationToken ct)
    {
        const string target = "iif";
        var docs = await Pending(db, target, from, to, includeSent, docOnly: true, ct);
        var pays = await PendingPay(db, target, from, to, includeSent, ct);
        var sb = new StringBuilder(); var items = new List<(string, int)>();
        var customers = new Dictionary<int, AcctCustomer>();
        async Task<AcctCustomer?> Cust(int id) { if (customers.TryGetValue(id, out var c)) return c; c = await AccountingData.Customer(db, id, ct); if (c is not null) customers[id] = c; return c; }
        var docList = new List<AcctDoc>(); foreach (var id in docs) if (await AccountingData.Document(db, id, ct) is { Voided: false } d) { docList.Add(d); await Cust(d.CustomerId); }
        var payList = new List<AcctPayment>(); foreach (var id in pays) if (await AccountingData.Payment(db, id, ct) is { Voided: false } p) { payList.Add(p); await Cust(p.CustomerId); }

        sb.AppendLine("!CUST\tNAME\tBADDR1\tBADDR2\tBADDR3\tBADDR4\tPHONE1\tEMAIL\tCONT1");
        foreach (var c in customers.Values)
        {
            sb.AppendLine(string.Join('\t', "CUST", T(AccountingData.CustomerName(cfg, c)), T(c.Display), T(c.Address1), T(c.Address2), T(string.Join(" ", new[] { c.City, c.State, c.Zip }.Where(x => !string.IsNullOrWhiteSpace(x)))), "", T(c.Phone), T(c.Email), T(c.Contact)));
            items.Add(("customer", c.Id));
        }
        sb.AppendLine("!TRNS\tTRNSTYPE\tDATE\tACCNT\tNAME\tAMOUNT\tDOCNUM\tPONUM\tMEMO");
        sb.AppendLine("!SPL\tTRNSTYPE\tDATE\tACCNT\tNAME\tAMOUNT\tDOCNUM\tMEMO\tQNTY");
        sb.AppendLine("!ENDTRNS");
        foreach (var d in docList)
        {
            var credit = d.Type == "C"; var sign = credit ? -1 : 1; var name = T(AccountingData.CustomerName(cfg, customers[d.CustomerId])); var type = credit ? "CREDIT MEMO" : "INVOICE";
            sb.AppendLine(string.Join('\t', "TRNS", type, D(d.Date), T(cfg.Ar), name, M(sign * d.Total), T(d.Number), T(d.Po), T(d.Notes)));
            foreach (var l in d.Lines.Where(l => l.Amount != 0))
                sb.AppendLine(string.Join('\t', "SPL", type, D(d.Date), T(cfg.Income), "", M(-sign * l.Amount), T(d.Number), T(string.Join(" - ", new[] { l.PartCode, l.Description }.Where(x => !string.IsNullOrWhiteSpace(x)))), l.Qty?.ToString() ?? ""));
            foreach (var t in d.Taxes.Where(t => t.Amount != 0))
                sb.AppendLine(string.Join('\t', "SPL", type, D(d.Date), T(cfg.TaxPayable), "", M(-sign * t.Amount), T(d.Number), T("Sales tax " + t.Code), ""));
            sb.AppendLine("ENDTRNS"); items.Add((d.Kind, d.Id));
        }
        foreach (var p in payList)
        {
            var name = T(AccountingData.CustomerName(cfg, customers[p.CustomerId])); var date = DateOnly.FromDateTime(p.PaidAt);
            sb.AppendLine(string.Join('\t', "TRNS", "PAYMENT", D(date), T(string.IsNullOrWhiteSpace(cfg.DepositAccount) ? cfg.Undeposited : cfg.DepositAccount), name, M(p.Amount), "P" + p.Id, "", T(p.Description ?? p.CategoryName)));
            sb.AppendLine(string.Join('\t', "SPL", "PAYMENT", D(date), T(cfg.Ar), name, M(-p.Amount), "P" + p.Id, "", ""));
            sb.AppendLine("ENDTRNS"); items.Add(("payment", p.Id));
        }
        return new("reflectopatronus-quickbooks.iif", "text/plain", sb.ToString(), docList.Count + payList.Count, items);
    }

    public static async Task<Result> SageInvoices(NpgsqlDataSource db, AccountingConfig cfg, DateOnly from, DateOnly? to, bool includeSent, CancellationToken ct)
    {
        var ids = await Pending(db, "sage50", from, to, includeSent, docOnly: true, ct); var sb = new StringBuilder(); var items = new List<(string, int)>();
        sb.AppendLine(Row("Customer ID", "Invoice/CM #", "Credit Memo", "Date", "Customer PO", "Accounts Receivable Account", "Accounts Receivable Amount", "Invoice Note", "Quantity", "Item ID", "Description", "G/L Account", "Unit Price", "Tax Type", "Amount"));
        var count = 0;
        foreach (var id in ids)
        {
            if (await AccountingData.Document(db, id, ct) is not { Voided: false } d) continue;
            var c = await AccountingData.Customer(db, d.CustomerId, ct); if (c is null) continue;
            var credit = d.Type == "C"; var sign = credit ? -1 : 1; var first = true;
            // Sage 50 reads one row per distribution line; the invoice header fields repeat on each row.
            foreach (var l in d.Lines.Where(l => l.Amount != 0).DefaultIfEmpty(new AcctLine(0, "other", null, "", null, 0, false)))
            {
                sb.AppendLine(Row(c.Number, d.Number, credit ? "TRUE" : "FALSE", d.Date.ToString("MM/dd/yyyy"), d.Po, cfg.Ar, first ? M(sign * d.Total) : "", d.Notes,
                    l.Qty?.ToString() ?? "", l.PartCode, l.Description, cfg.Income, l.Qty is > 0 ? M(l.Amount / l.Qty.Value) : "", l.Taxable ? "1" : "2", M(sign * l.Amount)));
                first = false;
            }
            foreach (var t in d.Taxes.Where(t => t.Amount != 0))
                sb.AppendLine(Row(c.Number, d.Number, credit ? "TRUE" : "FALSE", d.Date.ToString("MM/dd/yyyy"), d.Po, cfg.Ar, "", "", "", "", "Sales tax " + t.Code, cfg.TaxPayable, "", "", M(sign * t.Amount)));
            items.Add((d.Kind, d.Id)); count++;
        }
        return new("reflectopatronus-sage50-invoices.csv", "text/csv", sb.ToString(), count, items);
    }

    public static async Task<Result> SageReceipts(NpgsqlDataSource db, AccountingConfig cfg, DateOnly from, DateOnly? to, bool includeSent, CancellationToken ct)
    {
        var ids = await PendingPay(db, "sage50", from, to, includeSent, ct); var sb = new StringBuilder(); var items = new List<(string, int)>();
        sb.AppendLine(Row("Customer ID", "Reference", "Date", "Payment Method", "Cash Account", "Description", "Invoice Paid", "Amount Paid"));
        var count = 0;
        foreach (var id in ids)
        {
            if (await AccountingData.Payment(db, id, ct) is not { Voided: false } p) continue;
            var c = await AccountingData.Customer(db, p.CustomerId, ct); if (c is null) continue;
            var date = p.PaidAt.ToString("MM/dd/yyyy"); var cash = string.IsNullOrWhiteSpace(cfg.DepositAccount) ? cfg.Undeposited : cfg.DepositAccount;
            var applied = new List<(string Number, decimal Amount)>();
            foreach (var (docId, amount) in p.Applied) if (await AccountingData.Document(db, docId, ct) is { } d) applied.Add((d.Number, amount));
            if (applied.Count == 0) sb.AppendLine(Row(c.Number, "P" + p.Id, date, p.CategoryName, cash, p.Description, "", M(p.Amount)));
            else foreach (var a in applied) sb.AppendLine(Row(c.Number, "P" + p.Id, date, p.CategoryName, cash, p.Description, a.Number, M(a.Amount)));
            items.Add(("payment", p.Id)); count++;
        }
        return new("reflectopatronus-sage50-receipts.csv", "text/csv", sb.ToString(), count, items);
    }

    public static async Task<Result> SageCustomers(NpgsqlDataSource db, DateOnly from, DateOnly? to, bool includeSent, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT c.id FROM customer c WHERE ($1 OR NOT EXISTS (SELECT 1 FROM accounting_export e WHERE e.target = 'sage50' AND e.kind = 'customer' AND e.ref_id = c.id))
              AND EXISTS (SELECT 1 FROM document d WHERE coalesce(d.bill_to_customer_id, d.customer_id) = c.id AND d.doc_date >= $2 AND ($3::date IS NULL OR d.doc_date <= $3))
            ORDER BY c.number
            """);
        c.Parameters.AddWithValue(includeSent); c.Parameters.AddWithValue(from); c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = (object?)to ?? DBNull.Value });
        var ids = new List<int>(); await using (var r = await c.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        var sb = new StringBuilder(); var items = new List<(string, int)>();
        sb.AppendLine(Row("Customer ID", "Customer Name", "Contact", "Billing Address Line 1", "Billing Address Line 2", "City", "State", "Zip", "Telephone 1", "E-mail", "Resale Number", "Tax Exempt"));
        foreach (var id in ids)
        {
            if (await AccountingData.Customer(db, id, ct) is not { } x) continue;
            sb.AppendLine(Row(x.Number, x.Display, x.Contact, x.Address1, x.Address2, x.City, x.State, x.Zip, x.Phone, x.Email, x.TaxId, x.TaxExempt ? "TRUE" : "FALSE"));
            items.Add(("customer", id));
        }
        return new("reflectopatronus-sage50-customers.csv", "text/csv", sb.ToString(), items.Count, items);
    }

    static Task<List<int>> Pending(NpgsqlDataSource db, string target, DateOnly from, DateOnly? to, bool includeSent, bool docOnly, CancellationToken ct)
        => includeSent ? All(db, "SELECT d.id FROM document d WHERE d.doc_type IN ('I', 'C') AND NOT d.voided AND d.customer_id IS NOT NULL AND d.doc_date >= $1 AND ($2::date IS NULL OR d.doc_date <= $2) ORDER BY d.doc_date, d.id", from, to, ct)
                        : AccountingData.PendingDocuments(db, target, from, to, 100000, ct);
    static Task<List<int>> PendingPay(NpgsqlDataSource db, string target, DateOnly from, DateOnly? to, bool includeSent, CancellationToken ct)
        => includeSent ? All(db, "SELECT p.id FROM payment p WHERE NOT p.voided AND p.amount > 0 AND p.paid_at >= $1::date AND ($2::date IS NULL OR p.paid_at < $2::date + 1) ORDER BY p.paid_at, p.id", from, to, ct)
                        : AccountingData.PendingPayments(db, target, from, to, 100000, ct);
    static async Task<List<int>> All(NpgsqlDataSource db, string sql, DateOnly from, DateOnly? to, CancellationToken ct)
    {
        await using var c = db.CreateCommand(sql);
        c.Parameters.AddWithValue(from); c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = (object?)to ?? DBNull.Value });
        var l = new List<int>(); await using var r = await c.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) l.Add(r.GetInt32(0)); return l;
    }
}
