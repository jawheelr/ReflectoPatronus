using System.Security.Claims;
using Npgsql;

namespace ReflectoPatronus.Api.Auth;

public record PermissionDef(string Key, string Area, string Label);

/// <summary>
/// Role-based access. A user's permissions are the union of their groups' permissions; '*' (Administrators) allows everything.
/// Reading reference data (tax, categories, price lists, lookups, the company details) is open to every signed-in user, because
/// every screen needs it; everything that shows or changes customers, paperwork, money, prices or settings is gated here.
/// </summary>
public static class Permissions
{
    public const string All = "*";
    public static readonly PermissionDef[] Catalog =
    [
        new("customers.read", "Customers", "See customers"), new("customers.write", "Customers", "Add and change customers and their special prices"), new("customers.delete", "Customers", "Delete customers"),
        new("documents.read", "Paperwork", "See and print quotes, work orders, invoices and credit memos"), new("quotes.write", "Paperwork", "Write and change quotes"),
        new("workorders.write", "Paperwork", "Create and change work orders"), new("invoices.write", "Paperwork", "Create and change invoices"), new("credits.write", "Paperwork", "Create credit memos"),
        new("documents.void", "Paperwork", "Void documents"),
        new("receivables.read", "Money", "See balances, open invoices and payments"), new("payments.write", "Money", "Receive payments and apply credits"), new("payments.void", "Money", "Void payments"),
        new("catalog.write", "Prices and tables", "Change parts, prices and the waste table"), new("maintenance.write", "Prices and tables", "Change tax codes and groups, categories, salesmen and statuses"),
        new("pricing.manage", "Prices and tables", "Supplier pricing and cost increases"), new("inventory.manage", "Prices and tables", "Inventory, stock counts and purchase orders to suppliers"),
        new("reports.view", "Reports", "Run and print reports"),
        new("company.write", "Administration", "Change the company details"), new("users.manage", "Administration", "Add users, change groups and reset passwords"),
        new("accounting.manage", "Administration", "Send invoices and payments to accounting software"), new("transfer.manage", "Administration", "Export and import the database"), new("utilities.cleanup", "Administration", "Delete old quotes and unused customers"),
    ];
    public static readonly HashSet<string> Keys = Catalog.Select(p => p.Key).ToHashSet();

    const string ItemKey = "rp.permissions";

    /// <summary>The signed-in user's permissions, loaded once per request.</summary>
    public static async Task<HashSet<string>> Of(HttpContext ctx)
    {
        if (ctx.Items[ItemKey] is HashSet<string> cached) return cached;
        var set = await Load(ctx.RequestServices.GetRequiredService<NpgsqlDataSource>(), ctx.User.UserId(), ctx.RequestAborted);
        ctx.Items[ItemKey] = set; return set;
    }

    public static async Task<HashSet<string>> Load(NpgsqlDataSource db, int userId, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT DISTINCT gp.permission FROM user_group ug JOIN group_permission gp ON gp.group_id = ug.group_id
            JOIN app_user u ON u.id = ug.user_id WHERE ug.user_id = $1 AND u.active
            """);
        c.Parameters.AddWithValue(userId);
        var set = new HashSet<string>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    public static bool Allows(HashSet<string> have, params string[] anyOf) => have.Contains(All) || anyOf.Any(have.Contains);

    public static IResult Denied(params string[] anyOf)
        => Results.Json(new { error = "You do not have permission to do that.", needs = anyOf }, statusCode: 403);

    /// <summary>Handlers that only know the document type after loading it call this.</summary>
    public static async Task<bool> Has(HttpContext ctx, params string[] anyOf) => Allows(await Of(ctx), anyOf);

    /// <summary>Restricts a route group or endpoint to users holding ANY of the listed permissions. Everyone else gets 403.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder b, params string[] anyOf) where TBuilder : IEndpointConventionBuilder
        => b.AddEndpointFilter(async (ctx, next) => Allows(await Of(ctx.HttpContext), anyOf) ? await next(ctx) : Denied(anyOf));

    /// <summary>The permission that lets a user create or change a document of this type.</summary>
    public static string WriteFor(string docType) => docType switch { "Q" => "quotes.write", "W" => "workorders.write", "I" => "invoices.write", "C" => "credits.write", _ => "*" };
}
