using mykeepass.Services;

namespace mykeepass.UI;

/// <summary>
/// Owns the full interactive run loop after the database has been unlocked.
/// </summary>
public interface IUserInterface
{
    Task RunAsync(
        KeePassService     keepass,
        GoogleDriveService drive,
        string             fileId,
        string             fileName);
}
