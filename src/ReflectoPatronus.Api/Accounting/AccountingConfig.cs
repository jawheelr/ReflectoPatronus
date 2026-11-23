using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace ReflectoPatronus.Api.Accounting;

/// <summary>
/// Every field is optional: anything left blank falls back to a default name that QuickBooks can create for itself.
/// The only thing that matters to switch sending on is <see cref="Target"/>; <see cref="Since"/> defaults to the day it is switched on,
/// so years of old paperwork are never sent by accident.
/// </summary>
public sealed class AccountingConfig
{
    public string Target { get; set; } = "off";                  // off | qbd (QuickBooks Desktop pulls through the Web Connector)
    public string? Since { get; set; }                            // yyyy-MM-dd: only paperwork and payments on or after this date are sent
    public string? CustomerNaming { get; set; }                   // name-number (default, always unique) | name | number
    public string? IncomeAccount { get; set; }
    public string? TaxAgency { get; set; }
    public string? ArAccount { get; set; }                        // file exports (IIF) only; QuickBooks picks its own A/R account otherwise
    public string? TaxPayableAccount { get; set; }                // IIF
    public string? UndepositedAccount { get; set; }               // IIF, and the Web Connector's "deposit to" when DepositAccount is blank
    public string? DepositAccount { get; set; }
    public string? Terms { get; set; }
    public bool SendPaymentMethod { get; set; }                   // send the payment category as the QuickBooks payment method (it must already exist there)
    public Dictionary<string, string> Items { get; set; } = new();          // glass | part | labor | other -> QuickBooks item name
    public Dictionary<string, string> TaxItems { get; set; } = new();       // tax code -> QuickBooks sales tax item
    public Dictionary<string, string> TaxGroupItems { get; set; } = new();  // tax group -> QuickBooks sales tax group item
    public string? QbwcUser { get; set; }
    public string? QbwcPassword { get; set; }
    public string? PublicUrl { get; set; }
    public int? RunEveryMinutes { get; set; }
    public string? OwnerId { get; set; }

    public string Naming => CustomerNaming is "name" or "number" ? CustomerNaming : "name-number";
    public string Income => Pick(IncomeAccount, "Sales");
    public string Agency => Pick(TaxAgency, "Sales Tax Agency");
    public string Ar => Pick(ArAccount, "Accounts Receivable");
    public string TaxPayable => Pick(TaxPayableAccount, "Sales Tax Payable");
    public string Undeposited => Pick(UndepositedAccount, "Undeposited Funds");
    public string User => Pick(QbwcUser, "reflectopatronus");
    public int Every => Math.Clamp(RunEveryMinutes ?? 15, 1, 1440);
    public string Item(string source) => Items.TryGetValue(source, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : source switch { "glass" => "Glass", "part" => "Parts", "labor" => "Labor", _ => "Other" };
    public string TaxItem(string code) => TaxItems.TryGetValue(code, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : code;
    public string TaxGroupItem(string group) => TaxGroupItems.TryGetValue(group, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : group;
    static string Pick(string? v, string d) => string.IsNullOrWhiteSpace(v) ? d : v.Trim();
    public DateOnly SinceDate => DateOnly.TryParse(Since, out var d) ? d : DateOnly.FromDateTime(DateTime.Today);
    public bool On => Target == "qbd";

    /// <summary>Blank strings become null so a cleared field means "use the default".</summary>
    public void Tidy()
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        Since = N(Since); CustomerNaming = N(CustomerNaming); IncomeAccount = N(IncomeAccount); TaxAgency = N(TaxAgency); ArAccount = N(ArAccount); TaxPayableAccount = N(TaxPayableAccount)
            ; UndepositedAccount = N(UndepositedAccount); DepositAccount = N(DepositAccount); Terms = N(Terms); QbwcUser = N(QbwcUser); PublicUrl = N(PublicUrl)?.TrimEnd('/');
        Items = Clean(Items); TaxItems = Clean(TaxItems); TaxGroupItems = Clean(TaxGroupItems);
        if (Target != "qbd") Target = "off";
        if (Target == "qbd" && Since is null) Since = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    }
    static Dictionary<string, string> Clean(Dictionary<string, string>? d)
        => (d ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim());
}

public static class AccountingStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<AccountingConfig> Load(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var c = db.CreateCommand("SELECT value::text FROM accounting_setting WHERE key = 'config'");
        var v = await c.ExecuteScalarAsync(ct) as string;
        var cfg = v is null ? new AccountingConfig() : JsonSerializer.Deserialize<AccountingConfig>(v, Json) ?? new AccountingConfig();
        return cfg;
    }

    public static async Task Save(NpgsqlDataSource db, AccountingConfig cfg, CancellationToken ct = default)
    {
        cfg.Tidy();
        if (string.IsNullOrEmpty(cfg.OwnerId)) cfg.OwnerId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        if (string.IsNullOrEmpty(cfg.QbwcPassword)) cfg.QbwcPassword = NewPassword();
        await Put(db, "config", JsonSerializer.Serialize(cfg, Json), ct);
    }

    public static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    public static async Task Put(NpgsqlDataSource db, string key, string json, CancellationToken ct = default)
    {
        await using var c = db.CreateCommand("INSERT INTO accounting_setting (key, value) VALUES ($1, $2::jsonb) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()");
        c.Parameters.AddWithValue(key); c.Parameters.AddWithValue(json);
        await c.ExecuteNonQueryAsync(ct);
    }

    public static async Task<JsonElement?> Get(NpgsqlDataSource db, string key, CancellationToken ct = default)
    {
        await using var c = db.CreateCommand("SELECT value::text FROM accounting_setting WHERE key = $1");
        c.Parameters.AddWithValue(key);
        return await c.ExecuteScalarAsync(ct) is string s ? JsonDocument.Parse(s).RootElement.Clone() : null;
    }

    /// <summary>Record the outcome of one item. 'sent' also notes the QuickBooks id; every call counts an attempt.</summary>
    public static async Task Mark(NpgsqlDataSource db, string target, string kind, int refId, string status, string? externalId, string? note, CancellationToken ct = default)
    {
        await using var c = db.CreateCommand("""
            INSERT INTO accounting_export (target, kind, ref_id, status, external_id, note, attempts, sent_at)
            VALUES ($1, $2, $3, $4, $5, $6, 1, CASE WHEN $4 = 'sent' THEN now() END)
            ON CONFLICT (target, kind, ref_id) DO UPDATE SET status = EXCLUDED.status, external_id = coalesce(EXCLUDED.external_id, accounting_export.external_id),
                note = EXCLUDED.note, attempts = accounting_export.attempts + 1, updated_at = now(),
                sent_at = CASE WHEN EXCLUDED.status = 'sent' THEN now() ELSE accounting_export.sent_at END
            """);
        c.Parameters.AddWithValue(target); c.Parameters.AddWithValue(kind); c.Parameters.AddWithValue(refId); c.Parameters.AddWithValue(status);
        c.Parameters.AddWithValue((object?)externalId ?? DBNull.Value); c.Parameters.AddWithValue((object?)note ?? DBNull.Value);
        await c.ExecuteNonQueryAsync(ct);
    }

    public static async Task<string?> ExternalId(NpgsqlDataSource db, string target, string kind, int refId, CancellationToken ct = default)
    {
        await using var c = db.CreateCommand("SELECT external_id FROM accounting_export WHERE target = $1 AND kind = $2 AND ref_id = $3 AND status = 'sent'");
        c.Parameters.AddWithValue(target); c.Parameters.AddWithValue(kind); c.Parameters.AddWithValue(refId);
        return await c.ExecuteScalarAsync(ct) as string;
    }
}
