using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Vessel.Tests;

public partial class EvidenceStorageOutageTestsSupport
{
    internal const string Bucket = "private-bucket-name";
    internal const string Key = "owner/play/item.png";

    internal static AmazonS3Exception S3(HttpStatusCode status, string code) =>
        new($"{code} for {Bucket}/{Key} at https://private-endpoint.example") { StatusCode = status, ErrorCode = code };

    internal sealed class FailingS3(Func<Exception> failure)
        : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://localhost:9000", AuthenticationRegion = "us-east-1" })
    {
        public Exception? Thrown { get; private set; }

        private Task<T> Fail<T>()
        {
            Thrown = failure();
            return Task.FromException<T>(Thrown);
        }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken ct = default) => Fail<PutObjectResponse>();
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct = default) => Fail<GetObjectResponse>();
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken ct = default) => Fail<DeleteObjectResponse>();
    }
}
