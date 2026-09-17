using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepairRequest.Application.Attachments;

namespace RepairRequest.Infrastructure.Files;

/// <summary>Private file storage settings. The root must be an absolute directory outside any publicly served path.</summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string RootPath { get; set; } = string.Empty;
}

internal sealed class FileStorageOptionsValidator : IValidateOptions<FileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FileStorageOptions options) =>
        string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathFullyQualified(options.RootPath)
            ? ValidateOptionsResult.Fail($"{FileStorageOptions.SectionName}:RootPath must be an absolute directory path.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// Local private file storage adapter for development and tests (RR-ARCH-001 section 17 "local private storage
/// emulator/adapter"); a Blob Storage adapter replaces it in cloud environments. Keys are validated as server-generated
/// opaque identifiers, so no client value can reach the file system. Files are written to a temporary name and moved
/// into place without overwriting; the final path never contains a client filename or extension.
/// </summary>
internal sealed partial class LocalFileStorage : IFileStorage
{
    private readonly FileStorageOptions _options;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(IOptions<FileStorageOptions> options, ILogger<LocalFileStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken)
    {
        var path = PathFor(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(target, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new FileStream(PathFor(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            TryDelete(PathFor(storageKey));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Private file storage cleanup could not resolve a storage key.");
        }

        return Task.CompletedTask;
    }

    private string PathFor(string storageKey)
    {
        if (!StorageKeyPattern().IsMatch(storageKey))
        {
            throw new ArgumentException("Storage key is not a valid server-generated key.", nameof(storageKey));
        }

        var root = Path.GetFullPath(_options.RootPath);
        var parts = storageKey.Split('/');
        var path = Path.GetFullPath(Path.Combine(root, parts[0], parts[1]));

        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage key resolves outside the private storage root.", nameof(storageKey));
        }

        return path;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            // Never log file content or client names; the path is server-generated.
            _logger.LogWarning(exception, "Private file storage cleanup failed.");
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}/[0-9a-f]{32}$")]
    private static partial Regex StorageKeyPattern();
}
