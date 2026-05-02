namespace Aiwms.Core.Entities;

public class AiwmsOpenBox
{
    public string BoxNo { get; set; } = "";
    public string Contno { get; set; } = "";
    public string UserId { get; set; } = "";
    public string PalletType { get; set; } = "";
    public string? Division { get; set; }
    public string? Season { get; set; }
    public DateTime? LPMDt { get; set; }
    public string? ToteID { get; set; }
    public string? LogisticsBoxNo { get; set; }
    public DateTime CreateTS { get; set; }
    public List<AiwmsOpenBoxItem> Items { get; set; } = new();
}

public class AiwmsOpenBoxItem
{
    public long Id { get; set; }
    public string BoxNo { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public int Qty { get; set; }
    public int SrNo { get; set; }
    public string? Result { get; set; }
    public long? PCRowId { get; set; }
    public string? Size { get; set; }
    public string? Color { get; set; }
    public string? Style { get; set; }
    public string? GroupCode { get; set; }
    public string? Season { get; set; }
    public DateTime ScannedTS { get; set; }
}
