using System.Security.Cryptography;
using System.Text;

namespace Valafind.App.Updates;

/// <summary>
/// Lets one copy at a time install or update the application at an install location. Two copies started from a
/// download at the same moment (the file opened twice) used to race: one moved the old installation aside and its
/// new copy in, and the other then failed to move its own copy in ("a file that already exists cannot be created")
/// and kept running from the download.
/// </summary>
public sealed class InstallLock : IDisposable
{
    private readonly Mutex _mutex;

    private InstallLock(Mutex mutex, bool waited)
    {
        _mutex = mutex;
        Waited = waited;
    }

    /// <summary>True when another copy held the lock first: it was installing, so look again at what it installed.</summary>
    public bool Waited { get; }

    /// <summary>Takes the lock for <paramref name="target"/>, waiting at most <paramref name="timeout"/> for another
    /// copy to finish. Throws <see cref="TimeoutException"/> when it does not.</summary>
    public static InstallLock Acquire(string target, TimeSpan timeout)
    {
        var mutex = new Mutex(false, Name(target));
        try
        {
            var waited = false;
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
                if (!acquired)
                {
                    waited = true;
                    acquired = mutex.WaitOne(timeout);
                }
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // the other copy exited while installing: ours now
                waited = true;
            }
            if (!acquired)
                throw new TimeoutException("Another copy is still installing.");
            return new InstallLock(mutex, waited);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>One lock per install location (case-insensitive on Windows and macOS), for this user's session.</summary>
    internal static string Name(string target)
    {
        var path = Path.GetFullPath(target);
        if (!OperatingSystem.IsLinux())
            path = path.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
        return $"Local\\valafind-install-{hash}";
    }

    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not held by this thread (cannot happen with the using pattern; never fail a dispose for it).
        }
        _mutex.Dispose();
    }
}
