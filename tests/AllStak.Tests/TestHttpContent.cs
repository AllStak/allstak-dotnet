using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace AllStak.Tests;

internal static class TestHttpContent
{
    public static async Task<string> ReadDecodedStringAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null) return "";

        var bytes = await request.Content.ReadAsByteArrayAsync(ct);
        if (request.Content.Headers.ContentEncoding.Any(encoding =>
                string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase)))
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
