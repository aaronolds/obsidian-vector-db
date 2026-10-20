using Grpc.Core;
using Qdrant.Client;

namespace Knowledge.Qdrant;

/// <summary>A read-only Qdrant gRPC probe. Does not inspect or create collections.</summary>
public sealed class QdrantHealthCheck(string host, int port) : IDisposable
{
    private readonly QdrantClient client = new(host, port, grpcTimeout: TimeSpan.FromSeconds(3));

    public async Task<QdrantHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await client.HealthAsync(cancellationToken);
            return new QdrantHealth(true, null);
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Do not forward server error bodies or exception text to logs.
            return new QdrantHealth(false, ex.StatusCode.ToString());
        }
    }

    public void Dispose() => client.Dispose();
}

public sealed record QdrantHealth(bool IsReachable, string? FailureCode);
