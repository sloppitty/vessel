namespace Vessel.Application.Overview;

public sealed record SummaryReason(string Reason, int Count);
public sealed record SummaryCoverage(int ContributingAccountCount, int TotalAccountCount,
    int ContributingOperationCount, int ExcludedOperationCount, IReadOnlyList<SummaryReason> Reasons);
public sealed record SummaryAmount(string? AmountUsd, SummaryCoverage Coverage);
// An exact ratio avoids claiming an exact finite percentage for recurring divisions.
public sealed record BalanceShare(string Numerator, string Denominator);
public sealed record OverviewSummaryTotals(int AccountCount, SummaryAmount Balance, SummaryAmount Available,
    int OpenPositionCount, SummaryAmount UnrealizedPnl, SummaryAmount RealizedResult, int ImportedFillCount, int PaperExecutionCount = 0);
public sealed record OverviewSummaryRow(string Id, string Name, string? Icon, string? VenueId,
    OverviewSummaryTotals Totals, BalanceShare? ShareOfBalance);
public sealed record OverviewSummaryDto(string GroupBy, string Period, string Scope, DateTimeOffset AsOfUtc,
    DateTimeOffset? FromUtc, OverviewSummaryTotals Totals, IReadOnlyList<OverviewSummaryRow> Rows,
    int TotalGroupCount, int OmittedGroupCount, string ScopeNote, string TradingMode = "real");
