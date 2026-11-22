namespace ReflectoPatronus.Api.Auth;

public static class AdminFilter
{
    /// <summary>Administrators only: the '*' permission (the built-in Administrators group). Prefer a specific permission where one fits.</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder g) where TBuilder : IEndpointConventionBuilder => g.RequirePermission(Permissions.All);
}
