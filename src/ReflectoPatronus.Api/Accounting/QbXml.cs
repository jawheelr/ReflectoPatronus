using System.Globalization;
using System.Xml.Linq;

namespace ReflectoPatronus.Api.Accounting;

/// <summary>Builds QBXML requests. Element order matters to QuickBooks, so each builder follows the order of the QBXML spec.</summary>
public static class QbXml
{
    static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);
    static XElement Ref(string name, string full) => new(name, new XElement("FullName", AccountingData.Clip(full, 159)));
    static XElement? Opt(string name, string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : new XElement(name, Clip(v, max));
    static string Clip(string s, int n) { s = s.Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length <= n ? s : s[..n]; }

    public static string Envelope(string version, params XElement[] requests)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + new XDocument(new XProcessingInstruction("qbxml", $"version=\"{version}\""),
            new XElement("QBXML", new XElement("QBXMLMsgsRq", new XAttribute("onError", "continueOnError"), requests))).ToString(SaveOptions.DisableFormatting);

    static XElement Rq(string name, string id, XElement body) => new(name, new XAttribute("requestID", id), body);

    public static XElement Account(string name) => Rq("AccountAddRq", "setup-account", new XElement("AccountAdd", new XElement("Name", name), new XElement("AccountType", "Income")));
    public static XElement Vendor(string name) => Rq("VendorAddRq", "setup-vendor", new XElement("VendorAdd", new XElement("Name", name)));
    public static XElement ServiceItem(string name, string incomeAccount, string id)
        => Rq("ItemServiceAddRq", id, new XElement("ItemServiceAdd", new XElement("Name", Clip(name, 31)),
            new XElement("SalesOrPurchase", new XElement("Desc", Clip(name, 4095)), new XElement("Price", "0.00"), Ref("AccountRef", incomeAccount))));
    public static XElement SalesTaxItem(string name, decimal percent, string agency, string id)
        => Rq("ItemSalesTaxAddRq", id, new XElement("ItemSalesTaxAdd", new XElement("Name", Clip(name, 31)), new XElement("ItemDesc", Clip(name, 31)),
            new XElement("TaxRate", percent.ToString("0.####", CultureInfo.InvariantCulture)), Ref("TaxVendorRef", agency)));
    public static XElement SalesTaxGroup(string name, IEnumerable<string> members, string id)
        => Rq("ItemSalesTaxGroupAddRq", id, new XElement("ItemSalesTaxGroupAdd", new XElement("Name", Clip(name, 31)), new XElement("ItemDesc", Clip(name, 31)),
            members.Select(m => Ref("ItemSalesTaxRef", m))));

    public static XElement Customer(AccountingConfig cfg, AcctCustomer c)
        => Rq("CustomerAddRq", "customer-" + c.Id, new XElement("CustomerAdd",
            new XElement("Name", AccountingData.CustomerName(cfg, c)),
            Opt("FirstName", c.First, 25), Opt("LastName", c.Last, 25),
            Address("BillAddress", AccountingData.Clip(c.Display, 41), c.Address1, c.Address2, c.City, c.State, c.Zip),
            Opt("Phone", c.Phone, 21), Opt("Email", c.Email, 1023), Opt("Contact", c.Contact, 41),
            Opt("ResaleNum", c.TaxExempt ? c.TaxId : null, 15),
            Opt("AccountNumber", c.Number, 99)));

    static XElement? Address(string name, string? l1, string? l2, string? l3, string? city, string? state, string? zip)
    {
        var parts = new[] { l1, l2, l3 }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (parts.Length == 0 && string.IsNullOrWhiteSpace(city)) return null;
        var e = new XElement(name);
        for (var i = 0; i < Math.Min(parts.Length, 5); i++) e.Add(new XElement("Addr" + (i + 1), Clip(parts[i]!, 41)));
        if (!string.IsNullOrWhiteSpace(city)) e.Add(new XElement("City", Clip(city, 31)));
        if (!string.IsNullOrWhiteSpace(state)) e.Add(new XElement("State", Clip(state, 21)));
        if (!string.IsNullOrWhiteSpace(zip)) e.Add(new XElement("PostalCode", Clip(zip, 13)));
        return e;
    }

    /// <summary>The sales tax item (or group) a document is taxed with, or null when it carries no tax.</summary>
    public static string? TaxItemFor(AccountingConfig cfg, AcctDoc d)
        => d.Tax == 0 || d.Taxes.Count == 0 ? null : d.Taxes.Count == 1 ? cfg.TaxItem(d.Taxes[0].Code) : cfg.TaxGroupItem(d.TaxGroup ?? string.Join("+", d.Taxes.Select(t => t.Code)));

    public static XElement Invoice(AccountingConfig cfg, AcctDoc d, string customerName, AcctCustomer? c)
        => Txn(cfg, d, customerName, c, d.Kind == "credit" ? "CreditMemo" : "Invoice");

    static XElement Txn(AccountingConfig cfg, AcctDoc d, string customerName, AcctCustomer? c, string kind)
    {
        var taxItem = TaxItemFor(cfg, d);
        var add = new XElement(kind + "Add",
            Ref("CustomerRef", customerName),
            new XElement("TxnDate", d.Date.ToString("yyyy-MM-dd")),
            new XElement("RefNumber", Clip(d.Number, 11)),
            c is null ? null : Address("BillAddress", AccountingData.Clip(c.Display, 41), c.Address1, c.Address2, c.City, c.State, c.Zip),
            kind == "Invoice" ? new XElement("IsPending", "false") : null,
            kind == "Invoice" ? Opt("PONumber", d.Po, 25) : null,
            kind == "Invoice" && !string.IsNullOrWhiteSpace(cfg.Terms) ? Ref("TermsRef", cfg.Terms!) : null,
            taxItem is null ? null : Ref("ItemSalesTaxRef", taxItem),
            Opt("Memo", d.Notes, 4095));
        foreach (var l in d.Lines)
        {
            var desc = string.Join(" - ", new[] { l.PartCode, l.Description }.Where(s => !string.IsNullOrWhiteSpace(s)));
            add.Add(new XElement(kind + "LineAdd",
                Ref("ItemRef", cfg.Item(l.Source)),
                Opt("Desc", desc, 4095),
                l.Qty is > 0 ? new XElement("Quantity", l.Qty) : null,
                new XElement("Amount", Money(l.Amount)),
                taxItem is null ? new XElement("SalesTaxCodeRef", new XElement("FullName", "Non")) : new XElement("SalesTaxCodeRef", new XElement("FullName", l.Taxable ? "Tax" : "Non"))));
        }
        return Rq(kind + "AddRq", (kind == "Invoice" ? "invoice-" : "credit-") + d.Id, add);
    }

    public static XElement Payment(AccountingConfig cfg, AcctPayment p, string customerName, IReadOnlyList<(string TxnId, decimal Amount)> applied)
    {
        var add = new XElement("ReceivePaymentAdd",
            Ref("CustomerRef", customerName),
            new XElement("TxnDate", p.PaidAt.ToString("yyyy-MM-dd")),
            new XElement("RefNumber", "P" + p.Id),
            new XElement("TotalAmount", Money(p.Amount)),
            cfg.SendPaymentMethod && !string.IsNullOrWhiteSpace(p.CategoryName) ? Ref("PaymentMethodRef", p.CategoryName!) : null,
            Opt("Memo", p.Description, 4095),
            Ref("DepositToAccountRef", string.IsNullOrWhiteSpace(cfg.DepositAccount) ? cfg.Undeposited : cfg.DepositAccount!));
        if (applied.Count == 0) add.Add(new XElement("IsAutoApply", "false"));
        foreach (var (txn, amount) in applied)
            add.Add(new XElement("AppliedToTxnAdd", new XElement("TxnID", txn), new XElement("PaymentAmount", Money(amount))));
        return Rq("ReceivePaymentAddRq", "payment-" + p.Id, add);
    }

    public static XElement Void(string kind, int refId, string txnId)
        => Rq("TxnVoidRq", $"void-{kind}-{refId}", new XElement("TxnVoid",
            new XElement("TxnVoidType", kind switch { "credit" => "CreditMemo", "payment" => "ReceivePayment", _ => "Invoice" }), new XElement("TxnID", txnId)));

    /// <summary>One response from QuickBooks: the request it answers, its status, and the returned record.</summary>
    public record Reply(string RequestId, int Code, string Severity, string Message, XElement? Ret)
    {
        public bool Ok => Code == 0;
        public bool AlreadyExists => Code == 3100;
        public string? TxnId => Ret?.Element("TxnID")?.Value;
        public decimal? Number(string name) => decimal.TryParse(Ret?.Element(name)?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static List<Reply> Parse(string xml)
    {
        var list = new List<Reply>();
        try
        {
            var root = XDocument.Parse(xml).Root?.Element("QBXMLMsgsRs"); if (root is null) return list;
            foreach (var rs in root.Elements())
                list.Add(new(rs.Attribute("requestID")?.Value ?? "", int.TryParse(rs.Attribute("statusCode")?.Value, out var code) ? code : -1,
                    rs.Attribute("statusSeverity")?.Value ?? "", rs.Attribute("statusMessage")?.Value ?? "", rs.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Ret"))));
        }
        catch (System.Xml.XmlException) { /* a malformed reply is treated as no replies */ }
        return list;
    }
}
