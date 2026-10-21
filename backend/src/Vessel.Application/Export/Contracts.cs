using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Export;

/// <summary>A prepared export: its file name and the writer that streams its rows once the response starts.</summary>
public sealed record ExportFile(string FileName, string ContentType, Func<Stream, CancellationToken, Task> Write);

// Application owns this bounded port; EF details stay in Persistence. Reads are owner-filtered and stream in time order.
public interface IFactsExportStore
{
    Task<Account?> AccountAsync(Guid id, CancellationToken ct);
    IAsyncEnumerable<ImportedFill> FillsAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);
    IAsyncEnumerable<ManualExecution> ManualExecutionsAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);
    IAsyncEnumerable<FundingPayment> FundingAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);
}
