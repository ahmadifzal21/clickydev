using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Clicky.Windows.ScreenCapture;

/// <summary>
/// Grabs one frame of a whole monitor with Windows Graphics Capture. WGC reads what the compositor shows, so
/// GPU-drawn apps and HDR monitors come out right where GDI BitBlt can return black or washed-out frames.
/// WinRT has no way to make a D3D device or a capture item for a monitor, so those two steps go through the
/// documented interop entry points (d3d11.dll and IGraphicsCaptureItemInterop).
/// </summary>
internal sealed class WindowsGraphicsMonitorCapture : IDisposable
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(2);
    private readonly IDirect3DDevice direct3DDevice;

    public WindowsGraphicsMonitorCapture()
    {
        direct3DDevice = CreateDirect3DDevice();
    }

    public static bool IsSupportedOnThisMachine => GraphicsCaptureSession.IsSupported();

    /// <summary>
    /// Captures the monitor's current contents without the mouse pointer. The yellow capture border stays on:
    /// turning it off needs package identity (PORTING-PLAN step 9).
    /// </summary>
    public async Task<SoftwareBitmap> CaptureMonitorAsync(IntPtr monitorHandle, CancellationToken cancellationToken)
    {
        GraphicsCaptureItem monitorCaptureItem = CreateCaptureItemForMonitor(monitorHandle);
        using Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, numberOfBuffers: 1, monitorCaptureItem.Size);
        using GraphicsCaptureSession captureSession = framePool.CreateCaptureSession(monitorCaptureItem);
        captureSession.IsCursorCaptureEnabled = false;

        TaskCompletionSource firstFrameArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        framePool.FrameArrived += (_, _) => firstFrameArrived.TrySetResult();
        captureSession.StartCapture();
        await firstFrameArrived.Task.WaitAsync(FirstFrameTimeout, cancellationToken);

        using Direct3D11CaptureFrame capturedFrame = framePool.TryGetNextFrame()
            ?? throw new InvalidOperationException("Windows Graphics Capture signalled a frame but returned none.");
        // Copies the GPU texture into CPU memory before the frame (and its texture) goes back to the pool.
        return await SoftwareBitmap.CreateCopyFromSurfaceAsync(capturedFrame.Surface, BitmapAlphaMode.Premultiplied);
    }

    private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr monitorHandle)
    {
        IObjectReference activationFactory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        IGraphicsCaptureItemInterop captureItemInterop =
            (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(activationFactory.ThisPtr);
        Guid graphicsCaptureItemInterfaceId = GraphicsCaptureItemInterfaceId;
        IntPtr captureItemPointer = captureItemInterop.CreateForMonitor(monitorHandle, ref graphicsCaptureItemInterfaceId);
        try
        {
            return GraphicsCaptureItem.FromAbi(captureItemPointer);
        }
        finally
        {
            Marshal.Release(captureItemPointer);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        // Hardware first; WARP (software rasterizer) keeps capture working on machines without a usable GPU driver.
        int createDeviceResult = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out IntPtr d3d11DevicePointer, out _, out IntPtr immediateContextPointer);
        if (createDeviceResult < 0)
        {
            createDeviceResult = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out d3d11DevicePointer, out _, out immediateContextPointer);
        }

        Marshal.ThrowExceptionForHR(createDeviceResult);
        IntPtr dxgiDevicePointer = IntPtr.Zero;
        IntPtr winrtDevicePointer = IntPtr.Zero;
        try
        {
            Guid dxgiDeviceInterfaceId = DxgiDeviceInterfaceId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d11DevicePointer, ref dxgiDeviceInterfaceId, out dxgiDevicePointer));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePointer, out winrtDevicePointer));
            return MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevicePointer);
        }
        finally
        {
            if (winrtDevicePointer != IntPtr.Zero) Marshal.Release(winrtDevicePointer);
            if (dxgiDevicePointer != IntPtr.Zero) Marshal.Release(dxgiDevicePointer);
            Marshal.Release(immediateContextPointer);
            Marshal.Release(d3d11DevicePointer);
        }
    }

    public void Dispose() => direct3DDevice.Dispose();

    private static readonly Guid GraphicsCaptureItemInterfaceId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceInterfaceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr windowHandle, [In] ref Guid interfaceId);
        IntPtr CreateForMonitor([In] IntPtr monitorHandle, [In] ref Guid interfaceId);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr softwareModule, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int chosenFeatureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
