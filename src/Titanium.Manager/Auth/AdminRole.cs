namespace Titanium.Manager.Auth;

internal enum AdminRole
{
    Viewer = 0,
    Editor = 1,
    Admin = 2,
}

internal static class AdminRoleParser
{
    public static bool TryParse(string? value, out AdminRole role)
    {
        role = AdminRole.Viewer;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Equals("viewer", StringComparison.OrdinalIgnoreCase))
        {
            role = AdminRole.Viewer;
            return true;
        }

        if (value.Equals("editor", StringComparison.OrdinalIgnoreCase))
        {
            role = AdminRole.Editor;
            return true;
        }

        if (value.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            role = AdminRole.Admin;
            return true;
        }

        return false;
    }

    public static string ToApi(AdminRole role) => role switch
    {
        AdminRole.Editor => "editor",
        AdminRole.Admin => "admin",
        _ => "viewer",
    };
}
