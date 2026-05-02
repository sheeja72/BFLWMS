namespace Aiwms.Core;

public static class Roles
{
    public const string Admin        = "Admin";
    public const string WHAssociate  = "WHAssociate";
    public const string WHSupervisor = "WHSupervisor";
    public const string WHManager    = "WHManager";

    public const string AnyWarehouse = "Admin,WHAssociate,WHSupervisor,WHManager";
    public const string SupervisorOrAbove = "Admin,WHSupervisor,WHManager";
}

public static class AuthPolicies
{
    public const string RequireActiveUser = "RequireActiveUser";
}

public interface ICurrentUser
{
    string Name { get; }
    string? ClientIp { get; }
    string? ClientPcName { get; }
    string? Warehouse { get; }
    string? Country { get; }
    /// <summary>
    /// Awaits the AuthenticationStateProvider, reads the principal, then loads the
    /// user's Country/Warehouse from the DB. Caches the result on the instance.
    /// Pages should call this once in OnInitializedAsync before reading properties.
    /// </summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);
}
