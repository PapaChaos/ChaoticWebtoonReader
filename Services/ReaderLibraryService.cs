using System.IO.Compression;
using System.Net.Http.Headers;
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

    public Task<ReaderCacheState> GetCacheStateAsync()
    {
        Directory.CreateDirectory(_cacheRoot);

        var files = Directory.EnumerateFiles(_cacheRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .ToList();

        var archiveCount = files.Count(file => ArchiveExtensions.Contains(file.Extension));
        var totalBytes = files.Sum(file => file.Length);

        return Task.FromResult(new ReaderCacheState(_cacheRoot, archiveCount, totalBytes));
    }

    public Task<ReaderCacheState> ClearCacheAsync()
    {
        Directory.CreateDirectory(_cacheRoot);

        var activePath = _currentSession?.Kind == SourceKind.Archive
            ? Path.GetFullPath(_currentSession.StoragePath)
            : null;

        foreach (var file in Directory.EnumerateFiles(_cacheRoot, "*", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);
            if (activePath is not null && string.Equals(fullPath, activePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDelete(fullPath);
        }

        RemoveEmptyDirectories(_cacheRoot);
        return GetCacheStateAsync();
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
        var originalUri = uri;
        var googleDriveFileId = TryGetGoogleDriveFileId(uri, out var fileId)
            ? fileId
            : null;

        if (googleDriveFileId is not null)
        {
            uri = CreateGoogleDriveDownloadUri(googleDriveFileId);
        }

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

        if (IsHtmlResponse(response.Content.Headers.ContentType))
        {
            throw new ReaderSourceException("Google Drive did not return a downloadable archive. Make sure the file is shared with anyone who has the link.");
        }

        var fileName = GetDownloadFileName(response.Content.Headers.ContentDisposition, originalUri);
        var extension = Path.GetExtension(fileName);
        if (!ArchiveExtensions.Contains(extension))
        {
            extension = ".zip";
            fileName = $"{Path.GetFileNameWithoutExtension(fileName)}{extension}";
        }

        var storageName = googleDriveFileId is null
            ? $"{Guid.NewGuid():N}_{SanitizeFileName(fileName)}"
            : $"{googleDriveFileId}_{SanitizeFileName(fileName)}";

        var storagePath = Path.Combine(_cacheRoot, storageName);

        if (File.Exists(storagePath))
        {
            var existingFile = new FileInfo(storagePath);
            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength is null || existingFile.Length == expectedLength.Value)
            {
                return new DownloadedArchive(storagePath, originalUri.ToString(), Path.GetFileNameWithoutExtension(fileName));
            }
        }

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

        var title = string.IsNullOrWhiteSpace(fileName)
            ? uri.Host
            : Path.GetFileNameWithoutExtension(fileName);

        return new DownloadedArchive(storagePath, originalUri.ToString(), title);
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

    private static bool TryGetGoogleDriveFileId(Uri uri, out string fileId)
    {
        fileId = string.Empty;

        if (!uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "d", StringComparison.OrdinalIgnoreCase))
            {
                fileId = Uri.UnescapeDataString(segments[i + 1]);
                return !string.IsNullOrWhiteSpace(fileId);
            }
        }

        var queryId = GetQueryValue(uri.Query, "id");
        if (!string.IsNullOrWhiteSpace(queryId))
        {
            fileId = queryId;
            return true;
        }

        return false;
    }

    private static Uri CreateGoogleDriveDownloadUri(string fileId)
    {
        var escapedId = Uri.EscapeDataString(fileId);
        return new Uri($"https://drive.usercontent.google.com/download?id={escapedId}&export=download&confirm=t");
    }

    private static string? GetQueryValue(string query, string key)
    {
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var pairKey = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (!string.Equals(pairKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return parts.Length == 2
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
        }

        return null;
    }

    private static bool IsHtmlResponse(MediaTypeHeaderValue? contentType)
    {
        return contentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetDownloadFileName(ContentDispositionHeaderValue? contentDisposition, Uri uri)
    {
        var headerFileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(headerFileName))
        {
            return headerFileName.Trim('"');
        }

        var localName = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(localName)
            ? "download.zip"
            : localName;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character).ToArray());

        return string.IsNullOrWhiteSpace(sanitized)
            ? "download.zip"
            : sanitized;
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

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                // Cache cleanup should be best effort.
            }
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

public sealed record ReaderCacheState(string Location, int ArchiveCount, long TotalBytes);
