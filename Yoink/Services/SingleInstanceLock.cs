using System;
using System.IO;

namespace Yoink.Services;

/// <summary>
/// Whether this process is the one and only running copy of Yoink — needed once a second launch can
/// carry a URL that ought to reach the already-running instance instead of opening a second
/// independent window/tray-icon/queue.db writer (the Chrome native-messaging host, and a `.desktop`
/// file's <c>Exec=... %u</c>, can both launch Yoink again while it's already running).
///
/// Backed by a plain exclusively-opened <see cref="FileStream"/> (<see cref="FileShare.None"/>)
/// rather than a named <see cref="System.Threading.Mutex"/>: named cross-process <c>Mutex</c>
/// semantics on non-Windows .NET have a real "confirm it actually works here, don't just trust it"
/// history, whereas an exclusive file lock behaves identically and unambiguously on every OS this
/// app targets, and needs no <c>AbandonedMutexException</c> handling — a process that dies just drops
/// the OS-level lock, exactly like an abandoned mutex would, with nothing extra to catch.
/// </summary>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Returns the held lock if this process just became the sole instance, or null if another
    /// instance already holds it. The returned lock must be kept alive (not disposed, not
    /// garbage-collected) for as long as this process is meant to be "the" instance — releasing it
    /// early would let a second launch believe it's free to become primary too.
    /// </summary>
    public static SingleInstanceLock? TryAcquire()
    {
        var dir = Path.GetDirectoryName(SettingsService.SettingsPath)!;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "yoink.lock");

        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceLock(stream);
        }
        catch (IOException)
        {
            // Already locked by another instance.
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
