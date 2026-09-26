using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Gecko.Data.Documents;

/// <summary>A file the store kept: where it is, how big, and its hash (a swapped file is detectable).</summary>
public sealed record StoredFile(string Uri, long SizeBytes, byte[] Sha256);

/// <summary>
/// Where attachments (gate photos, survey pictures, scanned slips) live. The
/// database keeps only the reference — gecko_tos gate.attachment.blob_uri —
/// never the bytes: photos would dwarf the operational data and every backup.
///
/// Dev: <see cref="LocalFileStore"/>, a folder on disk. Azure: a Blob
/// implementation behind the same interface, chosen by configuration at deploy.
/// Every file is filed under its tenant, and the store refuses a reference that
/// points anywhere else.
/// </summary>
public interface IFileStore
{
    Task<StoredFile> SaveAsync(Guid tenantId, string folder, string extension, Stream content, CancellationToken ct);

    /// <summary>The file behind a reference, or null when it is gone. <paramref name="tenantId"/> must own it.</summary>
    Task<Stream?> OpenAsync(Guid tenantId, string uri, CancellationToken ct);
}

/// <summary>
/// Files under <c>FileStore:Root</c> (default <c>{content root}/_files</c>), as
/// <c>file:{tenant}/{folder}/{yyyy}/{MM}/{guid}{ext}</c>. The name is never the
/// uploader's: a path in a file name is how a store gets escaped.
/// </summary>
public sealed class LocalFileStore(string root) : IFileStore
{
    private const string Scheme = "file:";
    private readonly string _root = Path.GetFullPath(root);

    public async Task<StoredFile> SaveAsync(Guid tenantId, string folder, string extension, Stream content, CancellationToken ct)
    {
        if (folder.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("A folder is a plain word.", nameof(folder));
        if (extension.Length > 8 || !extension.StartsWith('.') || extension[1..].Any(c => !char.IsLetterOrDigit(c)))
            throw new ArgumentException("Not a file extension.", nameof(extension));

        var now = DateTime.UtcNow;
        var relative = $"{tenantId:N}/{folder}/{now:yyyy}/{now:MM}/{Guid.CreateVersion7():N}{extension.ToLowerInvariant()}";
        var path = Resolve(tenantId, relative) ?? throw new InvalidOperationException("Refused a path outside the store.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return new StoredFile(Scheme + relative, file.Length, sha.GetHashAndReset());
        }
    }

    public Task<Stream?> OpenAsync(Guid tenantId, string uri, CancellationToken ct)
    {
        if (!uri.StartsWith(Scheme, StringComparison.Ordinal)) return Task.FromResult<Stream?>(null);
        var path = Resolve(tenantId, uri[Scheme.Length..]);
        return Task.FromResult<Stream?>(path is not null && File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    /// <summary>Full path, or null if the reference would leave the tenant's own folder.</summary>
    private string? Resolve(Guid tenantId, string relative)
    {
        var tenantRoot = Path.Combine(_root, tenantId.ToString("N")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(tenantRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

public static class FileStoreRegistration
{
    public static IServiceCollection AddGeckoFileStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IFileStore>(sp =>
        {
            var root = configuration["FileStore:Root"];
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "_files");
            return new LocalFileStore(root);
        });
        return services;
    }
}
