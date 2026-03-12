using System.Security.Cryptography;

namespace DirectLink.Common;

public static class HashUtil
{
    public static async Task<string> ComputeSha256Async(string filePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        const int bufferSize = 64 * 1024;
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        var buffer = new byte[bufferSize];
        long totalRead = 0;
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += read;
            progress?.Report(totalRead);
            sha.TransformBlock(buffer, 0, read, buffer, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    public static string ComputeSha256Sync(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
