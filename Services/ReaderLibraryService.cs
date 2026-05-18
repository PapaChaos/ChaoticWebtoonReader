using System.IO.Compression;
using ChaoticWebtoonReader.Models;

namespace ChaoticWebtoonReader.Services;

public sealed class ReaderLibraryService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp",
        ".avif",
        ".tif",
        ".tiff"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".cbz"
    };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly string _cacheRoot;
    private ReaderSession? _currentSession;

    public ReaderLibraryService()
    {
        _cacheRoot = Path.Combine(FileSystem.AppDataDirectory, "Cache");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<ComicManifest> PickArchiveAsync()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Open comic archive",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".zip", ".cbz"],
                [DevicePlatform.Android] = ["application/zip", "application/x-zip-compressed", "application/vnd.comicbook+zip", "application/octet-stream"]
            })
        });

        if (file is null)
        {
            throw new OperationCanceledException();
        }

        var extension = Path.GetExtension(file.FileName);
        if (!ArchiveExtensions.Contains(extension))
        {
            throw new ReaderSourceException("Only .zip and .cbz files are supported right now.");
        }

        var storagePath = Path.Combine(_cacheRoot, $"{Guid.NewGuid():N}{extension}");

        await using (var input = await file.OpenReadAsync())
        await using (var output = File.Create(storagePath))
        {
            await input.CopyToAsync(output);
        }

        try
        {
            return OpenLocalSource(storagePath, file.FileName, Path.GetFileNameWithoutExtension(file.FileName));
        }
        catch
        {
            TryDelete(storagePath);
            throw;
        }
    }

    public async Task<ComicManifest> OpenSourceAsync(string? rawPath)
    {
        var input = NormalizeInput(rawPath);

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var downloaded = await DownloadArchiveAsync(uri);
            return OpenLocalSource(downloaded.StoragePath, downloaded.DisplayPath, downloaded.Title);
        }

        var path = NormalizeLocalPath(input);
        return OpenLocalSource(path, path, null);
    }

    public async Task<string> LoadPageDataUrlAsync(int pageIndex)
    {
        var session = _currentSession ?? throw new ReaderSourceException("No comic is open.");

        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ReaderSourceException("Page not found.");
        }

        var page = session.Pages[pageIndex];
        var bytes = await ReadPageBytesAsync(session, page);
        return $"data:{GetContentType(page.Extension)};base64,{Convert.ToBase64String(bytes)}";
    }

    private ComicManifest OpenLocalSource(string storagePath, string displayPath, string? titleOverride)
    {
        if (Directory.Exists(storagePath))
        {
            var root = Path.GetFullPath(storagePath);
            var pages = FindFolderPages(root);
            var title = titleOverride ?? new DirectoryInfo(root).Name;
            return CreateSession(title, SourceKind.Folder, root, displayPath, pages);
        }

        if (File.Exists(storagePath))
        {
            var extension = Path.GetExtension(storagePath);
            if (!ArchiveExtensions.Contains(extension))
            {
                throw new ReaderSourceException("Only folders, .zip files, and .cbz files are supported right now.");
            }

            IReadOnlyList<ComicPage> pages;
            try
            {
                pages = FindArchivePages(storagePath);
            }
            catch (InvalidDataException ex)
            {
                throw new ReaderSourceException($"The archive could not be read: {ex.Message}");
            }

            var title = titleOverride ?? Path.GetFileNameWithoutExtension(displayPath);
            return CreateSession(title, SourceKind.Archive, Path.GetFullPath(storagePath), displayPath, pages);
        }

        throw new ReaderSourceException("Source path was not found.");
    }

    private ComicManifest CreateSession(
        string title,
        SourceKind kind,
        string storagePath,
        string displayPath,
        IReadOnlyList<ComicPage> pages)
    {
        if (pages.Count == 0)
        {
            throw new ReaderSourceException("No readable image pages were found.");
        }

        var session = new ReaderSession(
            string.IsNullOrWhiteSpace(title) ? "Untitled comic" : title,
            kind,
            storagePath,
            displayPath,
            pages);

        _currentSession = session;

        return new ComicManifest(
            session.Title,
            session.Kind.ToString().ToLowerInvariant(),
            session.DisplayPath,
            session.Pages.Count,
            session.Pages);
    }

    private async Task<DownloadedArchive> DownloadArchiveAsync(Uri uri)
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ReaderSourceException($"Could not download the zip: {ex.Message}");
        }

        using var _ = response;

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (!ArchiveExtensions.Contains(extension))
        {
            extension = ".zip";
        }

        var storagePath = Path.Combine(_cacheRoot, $"{Guid.NewGuid():N}{extension}");

        try
        {
            await using var output = File.Create(storagePath);
            await response.Content.CopyToAsync(output);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            TryDelete(storagePath);
            throw new ReaderSourceException($"Could not save the downloaded zip: {ex.Message}");
        }
        catch
        {
            TryDelete(storagePath);
            throw;
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        var title = string.IsNullOrWhiteSpace(fileName)
            ? uri.Host
            : Path.GetFileNameWithoutExtension(fileName);

        return new DownloadedArchive(storagePath, uri.ToString(), title);
    }

    private static IReadOnlyList<ComicPage> FindFolderPages(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => ImageExtensions.Contains(Path.GetExtension(file)))
            .Select(file =>
            {
                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                return new PageCandidate(relativePath, new FileInfo(file).Length);
            })
            .OrderBy(page => page.Location, NaturalStringComparer.Instance)
            .Select((page, index) => ToPageSource(index, page.Location, page.Size))
            .ToList();
    }

    private static IReadOnlyList<ComicPage> FindArchivePages(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        return archive.Entries
            .Where(entry => entry.Length > 0 && ImageExtensions.Contains(Path.GetExtension(entry.FullName)))
            .Select(entry => new PageCandidate(entry.FullName.Replace('\\', '/'), entry.Length))
            .OrderBy(page => page.Location, NaturalStringComparer.Instance)
            .Select((page, index) => ToPageSource(index, page.Location, page.Size))
            .ToList();
    }

    private static ComicPage ToPageSource(int index, string location, long size)
    {
        return new ComicPage(
            index,
            Path.GetFileName(location),
            location,
            size,
            Path.GetExtension(location));
    }

    private static async Task<byte[]> ReadPageBytesAsync(ReaderSession session, ComicPage page)
    {
        if (session.Kind == SourceKind.Folder)
        {
            var fullPath = Path.GetFullPath(Path.Combine(
                session.StoragePath,
                page.Location.Replace('/', Path.DirectorySeparatorChar)));

            if (!IsInsideRoot(session.StoragePath, fullPath) || !File.Exists(fullPath))
            {
                throw new ReaderSourceException("Page file not found.");
            }

            return await File.ReadAllBytesAsync(fullPath);
        }

        using var archive = ZipFile.OpenRead(session.StoragePath);
        var entry = archive.GetEntry(page.Location);

        if (entry is null)
        {
            throw new ReaderSourceException("Archive page not found.");
        }

        await using var entryStream = entry.Open();
        await using var buffer = new MemoryStream();
        await entryStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string GetContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
    }

    private static string NormalizeInput(string? value)
    {
        var input = (value ?? string.Empty).Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ReaderSourceException("Enter a folder path, zip path, cbz path, or zip URL.");
        }

        return input;
    }

    private static string NormalizeLocalPath(string input)
    {
        var path = Environment.ExpandEnvironmentVariables(input);

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return Path.GetFullPath(path);
    }

    private static bool IsInsideRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache cleanup failure should not hide the real import error.
        }
    }

    private sealed record DownloadedArchive(string StoragePath, string DisplayPath, string Title);

    private sealed record ReaderSession(
        string Title,
        SourceKind Kind,
        string StoragePath,
        string DisplayPath,
        IReadOnlyList<ComicPage> Pages);

    private sealed record PageCandidate(string Location, long Size);

    private enum SourceKind
    {
        Folder,
        Archive
    }
}
