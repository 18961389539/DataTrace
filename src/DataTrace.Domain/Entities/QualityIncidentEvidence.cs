using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public sealed class QualityIncidentEvidence
{
    public long Id { get; set; }
    public long IncidentId { get; set; }
    public QualityIncident Incident { get; set; } = null!;
    public string MonthKey { get; set; } = "";
    public long RecordId { get; set; }
    public QualityEvidencePurpose Purpose { get; set; }
    public string SerialNo { get; set; } = "";
    public string PalletCode { get; set; } = "";
    public string StationCode { get; set; } = "";
    public string RecipeCode { get; set; } = "";
    public DateTime TriggerTime { get; set; }
    public Judgement Judgement { get; set; }
    public short ResultCode { get; set; }
    public string TagSnapshotJson { get; set; } = "[]";
}