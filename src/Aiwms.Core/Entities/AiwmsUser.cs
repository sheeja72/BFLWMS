namespace Aiwms.Core.Entities;

public class AiwmsUser
{
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Country { get; set; }
    public string? Warehouse { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<AiwmsUserRole> UserRoles { get; set; } = new();
}

public class AiwmsWHMaster
{
    public string Country { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
}

public class AiwmsRole
{
    public string RoleCode { get; set; } = "";
    public string RoleName { get; set; } = "";
    public DateTime CreateTS { get; set; }
}

public class AiwmsUserRole
{
    public string Username { get; set; } = "";
    public string RoleCode { get; set; } = "";
    public DateTime CreateTS { get; set; }
    public AiwmsUser? User { get; set; }
    public AiwmsRole? Role { get; set; }
}
