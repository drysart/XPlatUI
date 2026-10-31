using System.IO.Pipes;
using System.Text;

namespace XPlatUI;

/// <summary>
/// The cross-process claim behind <see cref="XPlatApplicationOptions.SingleInstance"/>: a named
/// mutex decides which process owns the application and a named pipe carries a later launch's
/// command line to it.
/// </summary>
/// <remarks>
/// <para>
/// A backend reaches for this when its platform has no single-instance mechanism of its own.
/// Windows has none, so it always does; macOS gets a Dock or Finder launch routed to the running
/// application for free, but a second launch of the executable itself needs it. GTK4 does not,
/// because <c>Gio.Application</c>'s own uniqueness is the only way to raise another process's
/// window under Wayland, so it keeps using that instead.
/// </para>
/// <para>
/// The mutex is the claim, so a crashed instance does not wedge the application: the next launch
/// finds an abandoned mutex, takes it over and becomes the owner. The pipe only carries the payload,
/// so an owner that cannot be reached leaves the second launch to exit quietly rather than starting
/// a competing process.
/// </para>
/// <para>
/// The name is scoped per user, so two users signed in at once each get their own instance.
/// </para>
/// </remarks>
public sealed class XPlatInstanceLock : IDisposable
{
    /// <summary>How long a second launch waits for the owning process to answer.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private NamedPipeServerStream? _server;
    private bool _disposed;

    private XPlatInstanceLock(string pipeName, Mutex mutex)
    {
        _pipeName = pipeName;
        _mutex = mutex;

        var listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "XPlatUI instance listener",
        };

        listener.Start();
    }

    /// <summary>
    /// Raised on a background thread when another launch of the application was forwarded to this
    /// one, with that launch's command line arguments.
    /// </summary>
    public event EventHandler<XPlatSecondInstanceEventArgs>? SecondInstance;

    /// <summary>
    /// Claims <paramref name="key"/> for this process, or forwards <paramref name="arguments"/> to
    /// the process that already owns it.
    /// </summary>
    /// <param name="key">The application's instance key, typically its name.</param>
    /// <param name="arguments">
    /// This launch's command line arguments, without the executable path. They are only sent when
    /// this process turns out to be a second instance.
    /// </param>
    /// <returns>
    /// The claim when this process is the owner, or null when another process owns the key and has
    /// been notified of this launch.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    public static XPlatInstanceLock? Acquire(string key, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(arguments);

        var name = ScopedName(key);

        // Windows separates session-local from global kernel objects by prefix, and a per-user
        // instance should stay session-local. Other platforms scope the name per user by hand.
        var mutexName = OperatingSystem.IsWindows() ? @"Local\" + name : name;
        var mutex = new Mutex(initiallyOwned: false, mutexName);

        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing; the claim is ours.
            owned = true;
        }

        if (owned)
        {
            return new XPlatInstanceLock(name, mutex);
        }

        mutex.Dispose();
        Forward(name, arguments);

        return null;
    }

    /// <summary>Releases the claim and stops listening for forwarded launches.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stop.Cancel();

        lock (_gate)
        {
            _server?.Dispose();
            _server = null;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was taken on a thread that has since ended; there is nothing to release.
        }

        _mutex.Dispose();
    }

    private static string ScopedName(string key) => $"XPlatUI.{Sanitise(key)}.{Sanitise(Environment.UserName)}";

    private static string Sanitise(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-');
        }

        return builder.Length == 0 ? "application" : builder.ToString();
    }

    private static void Forward(string pipeName, IReadOnlyList<string> arguments)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);

            var payload = Encoding.UTF8.GetBytes(string.Join('\0', arguments));
            client.Write(payload, 0, payload.Length);
            client.Flush();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The owner is starting up, busy or on its way out. The launch has still been handled:
            // starting a competing instance over an owner that cannot answer would be worse.
        }
    }

    private void Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            lock (_gate)
            {
                _server = server;
            }

            try
            {
                server.WaitForConnection();

                var payload = ReadAll(server);
                SecondInstance?.Invoke(this, new XPlatSecondInstanceEventArgs(Split(payload)));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // Dispose closes the server to end the wait; anything else means the client left.
            }
            finally
            {
                lock (_gate)
                {
                    _server = null;
                }

                server.Dispose();
            }
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];

        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string[] Split(byte[] payload) =>
        payload.Length == 0 ? [] : Encoding.UTF8.GetString(payload).Split('\0');
}
