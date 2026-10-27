using System;
using System.Runtime.InteropServices;
using Avalonia.Win32.DirectX;

namespace Avalonia.Win32.Direct3D12;

internal static unsafe class D3D12Interop
{
    public static readonly Guid IID_ID3D12Device = Guid.Parse("189819f1-1db6-4b57-be54-1821339b85f7");
    public static readonly Guid IID_ID3D12CommandQueue = Guid.Parse("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
    public static readonly Guid IID_ID3D12CommandAllocator = Guid.Parse("6102dee4-af59-4b09-b999-b44d73f09b24");
    public static readonly Guid IID_ID3D12GraphicsCommandList = Guid.Parse("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455");
    public static readonly Guid IID_ID3D12Fence = Guid.Parse("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
    public static readonly Guid IID_ID3D12Resource = Guid.Parse("696442be-a72e-4059-bc79-5b5c98040fad");
    public static readonly Guid IID_IDXGISwapChain3 = Guid.Parse("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

    [DllImport("d3d12.dll", ExactSpelling = true)]
    public static extern int D3D12CreateDevice(
        IntPtr pAdapter,
        D3D_FEATURE_LEVEL minimumFeatureLevel,
        in Guid riid,
        out IntPtr ppDevice);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, [MarshalAs(UnmanagedType.LPWStr)] string? lpName);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint D3D12_COMMAND_LIST_TYPE_DIRECT = 0;
    public const uint D3D12_COMMAND_QUEUE_FLAG_NONE = 0;
    public const uint D3D12_FENCE_FLAG_NONE = 0;

    public const uint D3D12_RESOURCE_STATE_COMMON = 0;
    public const uint D3D12_RESOURCE_STATE_PRESENT = 0;
    public const uint D3D12_RESOURCE_STATE_RENDER_TARGET = 4;
    public const uint D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES = 0xffffffff;

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12_COMMAND_QUEUE_DESC
    {
        public uint Type;
        public int Priority;
        public uint Flags;
        public uint NodeMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12_RESOURCE_TRANSITION_BARRIER
    {
        public IntPtr pResource;
        public uint Subresource;
        public uint StateBefore;
        public uint StateAfter;
    }

    // Native D3D12_RESOURCE_BARRIER is Type(4)+Flags(4)+union(~24) with trailing pad to 32 bytes.
    // Explicit layout mirrors the TRANSITION arm; pad keeps sizeof aligned with the C++ ABI.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct D3D12_RESOURCE_BARRIER
    {
        [FieldOffset(0)]
        public uint Type; // 0 = TRANSITION
        [FieldOffset(4)]
        public uint Flags; // 0 = NONE
        [FieldOffset(8)]
        public D3D12_RESOURCE_TRANSITION_BARRIER Transition;

        public static D3D12_RESOURCE_BARRIER CreateTransition(IntPtr pResource, uint stateBefore, uint stateAfter)
        {
            var barrier = new D3D12_RESOURCE_BARRIER();
            barrier.Type = 0;
            barrier.Flags = 0;
            barrier.Transition.pResource = pResource;
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrier.Transition.StateBefore = stateBefore;
            barrier.Transition.StateAfter = stateAfter;
            return barrier;
        }
    }

    public static int Release(IntPtr pUnk)
    {
        if (pUnk == IntPtr.Zero) return 0;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)pUnk)[2])(pUnk);
    }

    // ID3D12Device slots:
    public static int CreateCommandQueue(IntPtr device, in D3D12_COMMAND_QUEUE_DESC desc, in Guid riid, out IntPtr ppCommandQueue)
    {
        fixed (D3D12_COMMAND_QUEUE_DESC* pDesc = &desc)
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppQueue = &ppCommandQueue)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, void*, void*, void*, int>)(*(void***)device)[8])(device, pDesc, pRiid, ppQueue);
        }
    }

    public static int CreateCommandAllocator(IntPtr device, uint type, in Guid riid, out IntPtr ppCommandAllocator)
    {
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppAlloc = &ppCommandAllocator)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, void*, int>)(*(void***)device)[9])(device, type, pRiid, ppAlloc);
        }
    }

    public static int CreateCommandList(IntPtr device, uint nodeMask, uint type, IntPtr pCommandAllocator, IntPtr pInitialState, in Guid riid, out IntPtr ppCommandList)
    {
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppList = &ppCommandList)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, IntPtr, void*, void*, int>)(*(void***)device)[12])(device, nodeMask, type, pCommandAllocator, pInitialState, pRiid, ppList);
        }
    }

    public static int CreateFence(IntPtr device, ulong initialValue, uint flags, in Guid riid, out IntPtr ppFence)
    {
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppF = &ppFence)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, void*, void*, int>)(*(void***)device)[36])(device, initialValue, flags, pRiid, ppF);
        }
    }

    public static int GetDeviceRemovedReason(IntPtr device)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)device)[37])(device);
    }

    // ID3D12CommandQueue slots:
    public static void ExecuteCommandLists(IntPtr commandQueue, uint numCommandLists, IntPtr[] commandLists)
    {
        fixed (IntPtr* ppLists = commandLists)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, void>)(*(void***)commandQueue)[10])(commandQueue, numCommandLists, ppLists);
        }
    }

    public static int Signal(IntPtr commandQueue, IntPtr fence, ulong value)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)(*(void***)commandQueue)[14])(commandQueue, fence, value);
    }

    // ID3D12Fence slots:
    public static ulong GetCompletedValue(IntPtr fence)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, ulong>)(*(void***)fence)[8])(fence);
    }

    public static int SetEventOnCompletion(IntPtr fence, ulong value, IntPtr hEvent)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int>)(*(void***)fence)[9])(fence, value, hEvent);
    }

    // ID3D12CommandAllocator slots:
    public static int ResetCommandAllocator(IntPtr commandAllocator)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)commandAllocator)[8])(commandAllocator);
    }

    // ID3D12GraphicsCommandList slots:
    public static int CloseCommandList(IntPtr commandList)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)commandList)[9])(commandList);
    }

    public static int ResetCommandList(IntPtr commandList, IntPtr commandAllocator, IntPtr initialState)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)(*(void***)commandList)[10])(commandList, commandAllocator, initialState);
    }

    public static void ResourceBarrier(IntPtr commandList, uint numBarriers, in D3D12_RESOURCE_BARRIER barrier)
    {
        fixed (D3D12_RESOURCE_BARRIER* pBarrier = &barrier)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, void>)(*(void***)commandList)[26])(commandList, numBarriers, pBarrier);
        }
    }

    // IDXGISwapChain3::GetCurrentBackBufferIndex — call only on an IDXGISwapChain3 pointer (QI first).
    public static uint GetCurrentBackBufferIndex(IntPtr swapChain3)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)swapChain3)[36])(swapChain3);
    }
}
