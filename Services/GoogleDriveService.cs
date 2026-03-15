using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;

namespace mykeepass.Services;

/// <summary>
/// Handles all Google Drive interactions: OAuth2 authentication,
/// file search, in-memory download, and upload-in-place.
/// </summary>
public sealed class GoogleDriveService : IDisposable
{
    // Full Drive scope allows reading and writing any file the user owns.
    // drive.file scope would restrict access to files created by this app only,
    // which would block access to a .kdbx that was uploaded manually.
    private static readonly string[] Scopes = { DriveService.Scope.Drive };

    private readonly DriveService _drive;

    private GoogleDriveService(DriveService drive) => _drive = drive;

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs OAuth2 authentication and returns a ready-to-use service.
    /// On first run a browser window opens for the Google sign-in flow.
    /// The refresh token is persisted in %AppData%\MyKeePass so subsequent
    /// runs are completely silent.
    /// </summary>
    /// <param name="credentialsPath">
    /// Path to the OAuth2 client-secrets JSON downloaded from Google Cloud Console
    /// (APIs &amp; Services → Credentials → Download JSON).
    /// </param>
    public static async Task<GoogleDriveService> CreateAsync(
        string credentialsPath,
        CancellationToken ct = default)
    {
        // Step: Load OAuth2 client_id and client_secret from the downloaded JSON.
        await using var stream = new FileStream(
            credentialsPath, FileMode.Open, FileAccess.Read);

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            user: "user",                           // key used to look up the cached token
            taskCancellationToken: ct,
            dataStore: new FileDataStore("MyKeePass") // token persisted in %AppData%\MyKeePass
        );

        // Step: Build the Drive client with the authorised credential.
        var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "MyKeePass"
        });

        return new GoogleDriveService(service);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches Google Drive for a file matching <paramref name="fileName"/>
    /// (optionally inside <paramref name="folderPath"/>) and downloads its
    /// content directly into a <see cref="MemoryStream"/> — no temp file is
    /// written to disk. The returned stream is positioned at offset 0.
    /// </summary>
    /// <returns>
    /// <c>(stream, fileId)</c> when the file is found; <c>(null, null)</c> otherwise.
    /// </returns>
    public async Task<(MemoryStream? Stream, string? FileId)> DownloadFileAsync(
        string fileName,
        string? folderPath,
        CancellationToken ct = default)
    {
        // Step: Optionally resolve a Drive folder path to its folder ID.
        string? folderId = null;
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            folderId = await ResolvePathToFolderIdAsync(folderPath, ct);
            if (folderId is null)
                Console.Error.WriteLine(
                    $"Warning: Drive folder '{folderPath}' not found; " +
                    "searching entire Drive instead.");
        }

        // Step: Build a Drive Files.list query for the target filename.
        string query = $"name = '{EscapeQuery(fileName)}' and trashed = false";
        if (folderId is not null)
            query += $" and '{folderId}' in parents";

        var listReq = _drive.Files.List();
        listReq.Q          = query;
        listReq.Fields      = "files(id, name, size)";
        listReq.PageSize    = 10;
        listReq.OrderBy     = "modifiedTime desc"; // prefer the most recently modified copy

        var result = await listReq.ExecuteAsync(ct);

        if (result.Files is null || result.Files.Count == 0)
            return (null, null);

        var file = result.Files[0];
        Console.WriteLine($"  Drive reports file size: {file.Size?.ToString("N0") ?? "unknown"} bytes (id={file.Id})");

        // Step: Stream the file bytes directly into a MemoryStream.
        //       Files.Get targets the raw binary; for Google Workspace native
        //       formats (Docs, Sheets…) you would use Files.Export instead.
        var ms      = new MemoryStream();
        var getReq  = _drive.Files.Get(file.Id);
        var progress = await getReq.DownloadAsync(ms, ct);

        if (progress.Status != Google.Apis.Download.DownloadStatus.Completed)
        {
            await ms.DisposeAsync();
            throw new IOException(
                $"Download did not complete. Status={progress.Status}. " +
                $"{progress.Exception?.Message}");
        }

        ms.Position = 0; // reset so callers can read from the beginning
        return (ms, file.Id);
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the content of an existing Drive file (identified by
    /// <paramref name="fileId"/>) with the bytes from <paramref name="content"/>.
    /// The stream is read from position 0 regardless of its current position.
    /// </summary>
    public async Task UploadFileAsync(
        string fileId,
        string fileName,
        MemoryStream content,
        CancellationToken ct = default)
    {
        content.Position = 0;

        // Step: Update the file content while preserving its Drive metadata and parents.
        var meta = new Google.Apis.Drive.v3.Data.File { Name = fileName };

        var updateReq = _drive.Files.Update(
            meta,
            fileId,
            content,
            contentType: "application/octet-stream");

        updateReq.Fields = "id, name, size";

        var progress = await updateReq.UploadAsync(ct);

        if (progress.Status != UploadStatus.Completed)
            throw new IOException(
                $"Upload did not complete. Status={progress.Status}. " +
                $"{progress.Exception?.Message}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a slash-separated folder path (e.g. "Backups/KeePass") to the
    /// Drive folder ID of the deepest component, walking from Drive root.
    /// Returns <c>null</c> if any path component cannot be found.
    /// </summary>
    private async Task<string?> ResolvePathToFolderIdAsync(
        string path,
        CancellationToken ct)
    {
        string[] parts    = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string?  parentId = null; // null → Drive root

        foreach (string part in parts)
        {
            string q =
                $"name = '{EscapeQuery(part)}'" +
                $" and mimeType = 'application/vnd.google-apps.folder'" +
                $" and trashed = false";

            if (parentId is not null)
                q += $" and '{parentId}' in parents";

            var req = _drive.Files.List();
            req.Q        = q;
            req.Fields   = "files(id)";
            req.PageSize = 1;

            var res = await req.ExecuteAsync(ct);

            if (res.Files is null || res.Files.Count == 0)
                return null;

            parentId = res.Files[0].Id;
        }

        return parentId;
    }

    /// <summary>Escapes single-quotes inside Drive API query strings.</summary>
    private static string EscapeQuery(string value) => value.Replace("'", "\\'");

    public void Dispose() => _drive.Dispose();
}
