using Npgsql;

namespace ReflectoPatronus.Api.Auth;

public static class AdminFilter
{
    /// <summary>Restricts a route group or a single endpoint to administrators (app_user.is_admin). Everyone else gets 403.</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder g) where TBuilder : IEndpointConventionBuilder => g.AddEndpointFilter(async (ctx, next) =>
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
        await using var c = db.CreateCommand("SELECT is_admin FROM app_user WHERE id = $1");
        c.Parameters.AddWithValue(ctx.HttpContext.User.UserId());
        return (bool)(await c.ExecuteScalarAsync(ctx.HttpContext.RequestAborted) ?? false)
            ? await next(ctx) : Results.Json(new { error = "Administrator access required." }, statusCode: 403);
    });
}
