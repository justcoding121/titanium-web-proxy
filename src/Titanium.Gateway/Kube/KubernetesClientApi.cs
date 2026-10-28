using System.Text;
using k8s;

namespace Titanium.Gateway.Kube;

/// <summary>
///     <see cref="IKubeApi"/> over the official Kubernetes .NET client. The client supplies configuration (in-cluster
///     service account or kubeconfig), TLS trust, and token refresh; requests go through its authenticated
///     <see cref="HttpClient"/>.
/// </summary>
public sealed class KubernetesClientApi : IKubeApi, IDisposable
{
    private readonly Kubernetes _client;

    public KubernetesClientApi(Kubernetes client) => _client = client;

    /// <summary>In-cluster configuration when running in a pod, otherwise the default kubeconfig.</summary>
    public static KubernetesClientApi Create()
    {
        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildDefaultConfig();
        return new KubernetesClientApi(new Kubernetes(config));
    }

    public async Task<KubeResponse> SendAsync(HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_client.BaseUri, pathAndQuery));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
        }

        using var response = await _client.HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new KubeResponse(response.StatusCode, text);
    }

    public async Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_client.BaseUri, pathAndQuery));
        HttpResponseMessage? response = null;
        try
        {
            response = await _client.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new KubeApiException(response.StatusCode, text);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new OwnedStream(stream, response, request);
        }
        catch
        {
            response?.Dispose();
            request.Dispose();
            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Keeps the request and response alive for as long as the caller reads the stream.</summary>
    private sealed class OwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable[] _owners;

        public OwnedStream(Stream inner, params IDisposable[] owners)
        {
            _inner = inner;
            _owners = owners;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                foreach (var owner in _owners)
                {
                    owner.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
