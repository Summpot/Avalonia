using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using MicroCom.Runtime;
using Xunit;

namespace Avalonia.IntegrationTests.Win32.Infrastructure;

internal sealed unsafe class D3D12DebugLayer : IDisposable
{
    private readonly ID3D12InfoQueue? _infoQueue;
    private static int s_enabled;

    public static void Enable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || Interlocked.Exchange(ref s_enabled, 1) != 0)
            return;
        var iid = MicroComRuntime.GetGuidFor(typeof(ID3D12Debug));
        if (D3D12GetDebugInterface(in iid, out var pointer) == 0)
        {
            using var debug = MicroComRuntime.CreateProxyFor<ID3D12Debug>(pointer, true);
            debug.EnableDebugLayer();
        }
    }

    public D3D12DebugLayer(IntPtr device)
    {
        var iid = MicroComRuntime.GetGuidFor(typeof(ID3D12InfoQueue));
        if (Marshal.QueryInterface(device, in iid, out var pointer) == 0)
            _infoQueue = MicroComRuntime.CreateProxyFor<ID3D12InfoQueue>(pointer, true);
    }

    public void AssertNoErrors()
    {
        if (_infoQueue is null)
            return;
        var errors = new List<string>();
        for (ulong i = 0; i < _infoQueue.NumStoredMessages; i++)
        {
            IntPtr size = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(_infoQueue.GetMessage(i, null, &size));
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.ThrowExceptionForHR(_infoQueue.GetMessage(i, (void*)buffer, &size));
                var message = Marshal.PtrToStructure<DebugMessage>(buffer);
                // Skia queries GPU handles for CPU-only descriptor heaps (1315).
                // D3D11On12 probes D3D11 metadata on native D3D12 resources (916).
                if (message.Severity <= 1 && message.Id is not (1315 or 916))
                    errors.Add($"{message.Id}: {Marshal.PtrToStringAnsi(message.Description)}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        _infoQueue.ClearStoredMessages();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    public void Dispose() => _infoQueue?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct DebugMessage
    {
        public int Category, Severity, Id;
        public IntPtr Description, DescriptionLength;
    }

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12GetDebugInterface(in Guid iid, out IntPtr debug);
}
