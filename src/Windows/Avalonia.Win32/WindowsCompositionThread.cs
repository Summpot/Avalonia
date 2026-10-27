using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Logging;

namespace Avalonia.Win32;

/// <summary>
/// Owns a composition connection's initialization, message loop and cleanup on one thread.
/// </summary>
internal sealed class WindowsCompositionThread
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stopping = new();
    private Thread _thread = null!;
    private Action? _wake;
    private bool _finished;

    public CancellationToken CancellationToken { get; }

    private WindowsCompositionThread() => CancellationToken = _stopping.Token;

    public static T Start<T>(Func<WindowsCompositionThread, T> create, Action<T> run,
        Action<T?> cleanup, Action<T> wake) where T : class
    {
        var lifetime = new WindowsCompositionThread();
        var initialized = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime._thread = new Thread(() =>
        {
            T? connection = null;
            EventHandler onExit = (_, _) => lifetime.RequestStop();
            AppDomain.CurrentDomain.ProcessExit += onExit;
            try
            {
                connection = create(lifetime);
                lock (lifetime._sync)
                    lifetime._wake = () => wake(connection);
                initialized.SetResult(connection);
                run(connection);
            }
            catch (Exception e)
            {
                if (!initialized.TrySetException(e))
                    Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                        ?.Log(null, "Composition thread failed: {0}", e);
            }
            finally
            {
                lock (lifetime._sync)
                    lifetime._finished = true;
                try
                {
                    cleanup(connection);
                }
                catch (Exception e)
                {
                    Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                        ?.Log(null, "Failed to release composition connection: {0}", e);
                }
                finally
                {
                    AppDomain.CurrentDomain.ProcessExit -= onExit;
                    lifetime._stopping.Dispose();
                }
            }
        }) { IsBackground = true, Name = "DwmRenderTimerLoop" };
        if (OperatingSystem.IsWindows())
            lifetime._thread.SetApartmentState(ApartmentState.STA);
        lifetime._thread.Start();
        try
        {
            return initialized.Task.GetAwaiter().GetResult();
        }
        catch
        {
            lifetime._thread.Join();
            throw;
        }
    }

    private void RequestStop()
    {
        lock (_sync)
        {
            if (_finished)
                return;
            _stopping.Cancel();
            _wake?.Invoke();
        }
    }

    /// <summary>Requests cancellation and waits for thread-owned resources to be released.</summary>
    public void Stop()
    {
        RequestStop();
        if (Thread.CurrentThread != _thread)
            _thread.Join();
    }
}
