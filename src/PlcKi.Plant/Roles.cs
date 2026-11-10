using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace PlcKi.Plant;

/// <summary>What a role may do. Checked by the server (PlantHost.Gate and the change endpoints), not only hidden in the page.</summary>
public enum Permission
{
    /// <summary>Settings, logins, imports, hardware read, PLCSIM Start/Stop, storage: everything under Setup.</summary>
    Settings,
    /// <summary>Alarm notes and the shift handover note.</summary>
    Notes,
    /// <summary>Upload documents and change their details (not delete them).</summary>
    Documents,
    /// <summary>Change and hold values on a PLC that PLC-Ki has confirmed is PLCSIM.</summary>
    PlcsimValues,
    /// <summary>Change values on a real PLC (still only what ChangeRules allows, never what the safety program reads).</summary>
    RealValues,
}

/// <summary>
/// The three roles:
///   admin      - everything
///   technician - alarm notes, documents (upload and change), change and hold values on PLCSIM; no settings, no real PLC
///   operator   - every machine page, alarm notes; reads the PLC only
/// </summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Technician = "technician";
    public const string Operator = "operator";
    public static readonly IReadOnlyList<string> All = new[] { Admin, Technician, Operator };

    public static bool Can(string? role, Permission p) => role switch
    {
        Admin => true,
        Technician => p is Permission.Notes or Permission.Documents or Permission.PlcsimValues,
        Operator => p is Permission.Notes,
        _ => false,
    };

    /// <summary>"Administrator", "Technician", "Operator".</summary>
    public static string Title(string? role) => role switch
    {
        Admin => "Administrator",
        Technician => "Technician",
        _ => "Operator",
    };

    /// <summary>
    /// The calls under /api/admin that need less than an administrator, by method and path. Anything not on this
    /// list stays administrators only, so a new /api/admin endpoint is closed to the other roles until added here.
    /// </summary>
    private static readonly (string Method, Regex Path, Permission Need)[] AdminPathsForOthers =
    {
        ("GET", P("/api/admin/write/check"), Permission.PlcsimValues),
        ("GET", P("/api/admin/write/state"), Permission.PlcsimValues),
        ("GET", P("/api/admin/write/log"), Permission.PlcsimValues),
        ("POST", P("/api/admin/write"), Permission.PlcsimValues),
        ("GET", P("/api/admin/hold"), Permission.PlcsimValues),
        ("GET", P("/api/admin/hold/check"), Permission.PlcsimValues),
        ("POST", P("/api/admin/hold"), Permission.PlcsimValues),
        ("POST", P("/api/admin/hold/release"), Permission.PlcsimValues),
        ("PUT", P("/api/admin/documents/upload/[^/]+"), Permission.Documents),
        ("PATCH", P("/api/admin/documents/(?!upload$|scan$)[^/]+"), Permission.Documents),
    };

    private static Regex P(string path) => new("^" + path + "/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>May this role call this /api/admin method and path?</summary>
    public static bool MayCallAdminPath(string? role, string method, PathString path)
    {
        if (role == Admin) return true;
        var p = path.Value ?? "";
        foreach (var (m, re, need) in AdminPathsForOthers)
            if (string.Equals(m, method, StringComparison.OrdinalIgnoreCase) && re.IsMatch(p) && Can(role, need))
                return true;
        return false;
    }

    /// <summary>
    /// Why this role may not change a value on this target, or null when it may. <paramref name="target"/> is where the
    /// change would land after a fresh check: "plcsim" (confirmed), "real", or anything else (not known: refused).
    /// The rules of ChangeRules (what may be changed at all) apply on top of this, for every role.
    /// </summary>
    public static string? ValueChangeRefused(string? role, string? target) => target switch
    {
        "plcsim" when Can(role, Permission.PlcsimValues) => null,
        "real" when Can(role, Permission.RealValues) => null,
        "plcsim" => "Your login only reads the PLC. A technician or an administrator can change values on PLCSIM.",
        "real" when Can(role, Permission.PlcsimValues) => "A technician can change values on PLCSIM only. On a real PLC, ask an administrator.",
        "real" => "Your login only reads the PLC. Only an administrator can change values on a real PLC.",
        _ when role == Admin => null,     // the change rules already say why (no PLC, not allowed, ...)
        _ => "PLC-Ki can't confirm this PLC is PLCSIM, so only an administrator may try a change here.",
    };
}
