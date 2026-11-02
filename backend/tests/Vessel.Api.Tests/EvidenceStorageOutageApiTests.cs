using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Evidence;
using Vessel.Domain.Evidence;
using Vessel.Infrastructure.Evidence;

namespace Vessel.Tests;

/// <summary>
/// Transient object-storage failures become <see cref="EvidenceStorageUnavailableException"/> (503 in the API);
/// configuration, permission, missing-object and create-only conflicts keep their original behaviour.
/// </summary>
public sealed class EvidenceStorageOutageApiTests : EvidenceStorageOutageTestsSupport
{

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", HttpStatusCode.ServiceUnavailable, "The service is unavailable. Try again later.")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", HttpStatusCode.ServiceUnavailable, "The service is unavailable. Try again later.")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", HttpStatusCode.InternalServerError, "An unexpected error occurred.")]
    public async Task Api_reports_storage_outages_as_503_without_provider_details(HttpStatusCode s3Status, string code,
        HttpStatusCode expected, string detail)
    {
        var owner = Guid.NewGuid();
        var metadata = new MemoryEvidenceStore(owner);
        var play = EvidenceFixtures.PlayFor(owner);
        metadata.Plays.Add(play);
        using var s3 = new FailingS3(() => S3(s3Status, code));
        await using var factory = new CoreApiFactory(owner, configure: services =>
        {
            services.AddSingleton<IEvidenceMetadataStore>(metadata);
            services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            services.AddSingleton<IEvidenceObjectStore>(new S3EvidenceObjectStore(s3, Bucket));
        });
        using var client = factory.AuthorizedClient();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(EvidenceFixtures.Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "chart.png");
        using var response = await client.PostAsync($"/api/plays/{play.Id}/evidence", form);
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(detail, body);
        foreach (var secret in new[] { Bucket, "private-endpoint", owner.ToString("N"), play.Id.ToString("N"), code, "Amazon", "bucket" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(metadata.Items);
    }

    /// <summary>
    /// Phase 0.6 (D-10): a Play's evidence metadata comes from the database alone, so the journal opens during an
    /// outage; only the affected original fails, as 503 when transient and 404 when the object is gone, and a failed
    /// read never removes the record.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", HttpStatusCode.NotFound)]
    public async Task Api_lists_metadata_during_an_outage_and_fails_only_the_original_read(HttpStatusCode s3Status, string code,
        HttpStatusCode expected)
    {
        var owner = Guid.NewGuid();
        var metadata = new MemoryEvidenceStore(owner);
        var play = EvidenceFixtures.PlayFor(owner);
        metadata.Plays.Add(play);
        var id = Guid.NewGuid();
        metadata.Items.Add(new PlayEvidence(id, play, $"{owner:N}/{play.Id:N}/{id:N}.png", "image/png", EvidenceFixtures.Png.Length,
            new string('a', 64), EvidenceSource.Upload, "Range high", DateTimeOffset.Parse("2026-10-19T10:00:00Z")));
        using var s3 = new FailingS3(() => S3(s3Status, code));
        await using var factory = new CoreApiFactory(owner, configure: services =>
        {
            services.AddSingleton<IEvidenceMetadataStore>(metadata);
            services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            services.AddSingleton<IEvidenceObjectStore>(new S3EvidenceObjectStore(s3, Bucket));
        });
        using var client = factory.AuthorizedClient();

        using var list = await client.GetAsync($"/api/plays/{play.Id}/evidence");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = await list.Content.ReadAsStringAsync();
        Assert.Contains(id.ToString(), listed);
        Assert.Contains("Range high", listed);
        Assert.Null(s3.Thrown);

        using var content = await client.GetAsync($"/api/evidence/{id}/content");
        Assert.Equal(expected, content.StatusCode);
        Assert.NotNull(s3.Thrown);
        var body = await content.Content.ReadAsStringAsync();
        foreach (var secret in new[] { Bucket, "private-endpoint", owner.ToString("N"), play.Id.ToString("N"), code, "Amazon", "bucket" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(id, Assert.Single(metadata.Items).Id);
    }
}
