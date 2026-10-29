using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.MicroCom;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT;

/// <summary>Owns a dispatcher queue on the calling thread, including its asynchronous shutdown.</summary>
internal sealed class WinRTDispatcherQueue : IDisposable
{
    private IDispatcherQueueController? _controller;

    public WinRTDispatcherQueue()
    {
        var pointer = NativeWinRTMethods.CreateDispatcherQueueController(new NativeWinRTMethods.DispatcherQueueOptions
        {
            apartmentType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
            dwSize = Marshal.SizeOf<NativeWinRTMethods.DispatcherQueueOptions>(),
            threadType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT
        });
        _controller = MicroComRuntime.CreateProxyFor<IDispatcherQueueController>(pointer, true);
    }

    public void Dispose()
    {
        var controller = _controller;
        if (controller is null)
            return;
        _controller = null;
        using (controller)
        using (var window = new SimpleWindow(null))
        using (var shutdown = controller.ShutdownQueueAsync())
        {
            using var completion = new ShutdownCompletion(window.Handle);
            shutdown.SetCompleted(completion);
            // A current-thread dispatcher must keep pumping until shutdown has drained its work.
            while (!completion.IsCompleted)
            {
                var result = UnmanagedMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result < 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (result > 0)
                    UnmanagedMethods.DispatchMessage(ref message);
            }
            shutdown.GetResults();
        }
    }

    private sealed class ShutdownCompletion : CallbackBase, IAsyncActionCompletedHandler
    {
        private readonly IntPtr _window;
        public volatile bool IsCompleted;

        public ShutdownCompletion(IntPtr window) => _window = window;

        public void Invoke(IAsyncAction asyncInfo, AsyncStatus asyncStatus)
        {
            IsCompleted = true;
            // Completion can run inside GetMessage, so it also needs a message to make that call return.
            UnmanagedMethods.PostMessage(_window, (uint)UnmanagedMethods.WindowsMessage.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
