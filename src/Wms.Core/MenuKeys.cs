namespace Wms.Core;

/// <summary>
/// Menu key constants — one per nav item that participates in the
/// per-user menu-access grants. Mirrored in [Authorize(Policy=…)]
/// on each page and in NavMenu's per-item AuthorizeView.
/// </summary>
public static class MenuKeys
{
    public const string LPM_MANUAL_BUILDING       = "LPM_MANUAL_BUILDING";
    public const string CONTAINER_ALLOCATION      = "CONTAINER_ALLOCATION";
    public const string ITEM_ENCODING             = "ITEM_ENCODING";
    public const string LPM_PRODUCTION            = "LPM_PRODUCTION";

    public const string RPT_MISSING_EXCESS        = "RPT_MISSING_EXCESS";
    public const string RPT_NON_LPM_WH_STOCK      = "RPT_NON_LPM_WH_STOCK";
    public const string RPT_LPM_WH_STOCK          = "RPT_LPM_WH_STOCK";
    public const string RPT_PRODUCTION_SUMMARY    = "RPT_PRODUCTION_SUMMARY";
    public const string RPT_WAREHOUSE_BOXES       = "RPT_WAREHOUSE_BOXES";

    public const string ADMIN_USERS               = "ADMIN_USERS";
    public const string ADMIN_WH_MASTER           = "ADMIN_WH_MASTER";
    public const string ADMIN_AUDIT_LOG           = "ADMIN_AUDIT_LOG";
    public const string ADMIN_NIGHTLY_BATCHES     = "ADMIN_NIGHTLY_BATCHES";

    /// <summary>One catalogue entry per menu item.</summary>
    public sealed record MenuEntry(
        string Key,
        string Group,
        string Label,
        string Url,
        IReadOnlyList<string> DefaultRoles);

    /// <summary>
    /// Source of truth for the menu inventory. Each entry's DefaultRoles
    /// drives the "natural" access (current pre-grant behaviour); explicit
    /// per-user grants in WmsUserMenuAccess are layered ON TOP.
    /// </summary>
    public static readonly IReadOnlyList<MenuEntry> All = new[]
    {
        new MenuEntry(LPM_MANUAL_BUILDING,   "Container Building",  "LPM Manual Building",       "building/manual",           new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),
        new MenuEntry(CONTAINER_ALLOCATION,  "Container Building",  "Container Allocation",      "building/container-allocation", new[] { Roles.Admin, Roles.WHManager }),

        new MenuEntry(ITEM_ENCODING,         "Item Encoding",       "Item Encoding",             "encoding",                  new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),

        new MenuEntry(LPM_PRODUCTION,        "Production to Stores","LPM Production",            "production/lpm",            new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),

        new MenuEntry(RPT_MISSING_EXCESS,    "Reports",             "Missing / Excess Items from Production", "reports/missing-excess",   new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_NON_LPM_WH_STOCK,  "Reports",             "Non-LPM WH Stock Report",   "reports/non-lpm-wh-stock",  new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_LPM_WH_STOCK,      "Reports",             "LPM WH Stock Report",       "reports/lpm-wh-stock",      new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_PRODUCTION_SUMMARY,"Reports",             "Production Summary Report", "reports/production-summary",new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_WAREHOUSE_BOXES,   "Reports",             "Warehouse Boxes",           "reports/warehouse-boxes",   new[] { Roles.Admin, Roles.Reports }),

        new MenuEntry(ADMIN_USERS,           "Admin",               "Users & Roles",             "admin/users",               new[] { Roles.Admin }),
        new MenuEntry(ADMIN_WH_MASTER,       "Admin",               "WH Master",                 "admin/wh-master",           new[] { Roles.Admin }),
        new MenuEntry(ADMIN_AUDIT_LOG,       "Admin",               "Audit Log",                 "admin/audit",               new[] { Roles.Admin }),
        new MenuEntry(ADMIN_NIGHTLY_BATCHES, "Admin",               "Nightly Batches Status",    "admin/nightly-batches",     new[] { Roles.Admin }),
    };

    /// <summary>Claim type emitted per granted menu by WmsClaimsTransformer.</summary>
    public const string ClaimType = "aiwms_menu";
}
