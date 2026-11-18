using Vessel.Application.Review;
using Vessel.Domain.Reporting;

namespace Vessel.Tests;

/// <summary>
/// Test-only defaults for <see cref="IReviewStore"/> (issue #114): the windowed read is the plain read, and the reporting
/// setting is absent (UTC, Monday).
/// </summary>
public interface IReviewStoreDefaults : IReviewStore
{
    Task<ReviewFacts> IReviewStore.ReadAsync(ReviewQuery query, DateTimeOffset asOf, ReviewWindow window, CancellationToken ct) =>
        ReadAsync(query, asOf, ct);

    Task<ReportingSettings?> IReviewStore.ReportingAsync(CancellationToken ct) => Task.FromResult<ReportingSettings?>(null);
}
