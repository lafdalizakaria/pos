using Microsoft.Extensions.Options;
using Newrest.Pos.Application.Abstractions;

namespace Newrest.Pos.Infrastructure.Storage;

public sealed class FileStorageOptions
{
    public const string Section = "Storage";

    /// <summary>Root folder for reference photos and datasets. Relative paths are resolved from the content root.</summary>
    public string RootPath { get; set; } = "data/storage";
}

/// <summary>
/// Disk storage keyed by relative paths (<c>articles/CSC-VND/xxx.jpg</c>). Keys are validated so that they can never
/// escape <see cref="FileStorageOptions.RootPath"/>. A blob storage implementation can replace it without code changes.
/// </summary>
public sealed class LocalFileStorage(IOptions<FileStorageOptions> options) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Path.IsPathRooted(key) || key.Contains("..", StringComparison.Ordinal)
            || key.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return full;
    }
}
