namespace Tagium.Core.Files;

/// <summary>
/// What the scanner compares to decide whether a catalogued file has changed: its length and
/// its last-write time in Unix seconds. Both halves are always read together, because comparing
/// only one of them is how a truncated-then-touched file slips through as unchanged.
/// </summary>
public readonly record struct FileIdentity(long Size, long ModifiedTimeUtc)
{
    /// <summary>
    /// Reads the identity of a file. Returns <c>null</c> when the file cannot be inspected —
    /// it vanished between enumeration and this call, or the path is unreadable — rather than
    /// throwing, because a scan treats an unreadable entry as "skip it", not as a failure.
    /// </summary>
    public static FileIdentity? TryRead(string path)
    {
        try
        {
            FileInfo info = new(path);
            return new FileIdentity(info.Length, ToUnixSeconds(info.LastWriteTimeUtc));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads the identity of a file that must exist; throws if it cannot be inspected.</summary>
    public static FileIdentity Read(string path)
    {
        FileInfo info = new(path);
        return new FileIdentity(info.Length, ToUnixSeconds(info.LastWriteTimeUtc));
    }

    public static long ToUnixSeconds(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
}
