namespace Aiwms.Core.Entities;

public class AiwmsAuditLog
{
    public long Id { get; set; }
    public string EntityName { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public char Action { get; set; } // I,U,D,X
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedTS { get; set; }
    public string? ClientIp { get; set; }
    public string? Context { get; set; }
    public string? ChangesJson { get; set; }
}

public class AppConfig
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public DateTime UpdatedTS { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public class AiwmsBoxSequence
{
    public string Contno { get; set; } = "";
    public int NextSeq { get; set; }
    public DateTime UpdatedTS { get; set; }
}

public class AiwmsContainerPhotoCheck
{
    public string Contno { get; set; } = "";
    public int PhotoQty { get; set; }
    public int OrgQty { get; set; }
    public bool Matched { get; set; }
    public DateTime CheckedTS { get; set; }
    public string CheckedBy { get; set; } = "";
}
