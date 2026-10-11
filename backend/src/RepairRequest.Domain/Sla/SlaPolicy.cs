namespace RepairRequest.Domain.Sla;

/// <summary>
/// The V1 SLA clock policy (`docs/16` D5–D7; owner label `SLA-004/v1`): 24/7 elapsed UTC time, no working calendar,
/// holiday or Site time zone. Due is the start plus 24 hours; the risk point is the due time minus 4 hours.
/// </summary>
public static class SlaPolicy
{
    public const string Version = "SLA-004/v1";

    public const int VersionMaxLength = 30;

    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static readonly TimeSpan RiskLead = TimeSpan.FromHours(4);

    public static DateTime DueAt(DateTime startedAt) => startedAt + Window;

    public static DateTime RiskAt(DateTime dueAt) => dueAt - RiskLead;
}
