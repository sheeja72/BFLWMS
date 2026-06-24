namespace Wms.Core.Entities;

public class WmsUser
{
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Country { get; set; }
    public string? Warehouse { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<WmsUserRole> UserRoles { get; set; } = new();
}

public class WmsWHMaster
{
    public string Country { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
}

public class WmsRole
{
    public string RoleCode { get; set; } = "";
    public string RoleName { get; set; } = "";
    public DateTime CreateTS { get; set; }
}

public class WmsUserRole
{
    public string Username { get; set; } = "";
    public string RoleCode { get; set; } = "";
    public DateTime CreateTS { get; set; }
    public WmsUser? User { get; set; }
    public WmsRole? Role { get; set; }
}

public class WmsUserMenuAccess
{
    public string   Username  { get; set; } = "";
    public string   MenuKey   { get; set; } = "";
    public DateTime GrantedTS { get; set; }
    public string   GrantedBy { get; set; } = "";
}
