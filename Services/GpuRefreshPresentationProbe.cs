using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Opt-in pixel mapping diagnostic. Captures only its own synthetic, never activated,
    /// wholly offscreen window through WGC; it never captures the desktop or a user window.
    /// </summary>
    internal static class GpuRefreshPresentationProbe
    {
        internal static async Task<object> RunAsync()
        {
            if (!GraphicsCaptureSession.IsSupported())
                return new { Supported = false, Reason = "Windows Graphics Capture is unavailable." };
            // WGC's treatment of the window backdrop is not the swap chain's alpha contract.
            // Compare two otherwise identical native windows instead of assuming either an
            // opaque black background or that capture bytes are premultiplied GPU bytes.
            ProbeCase baseline = await RunCaseAsync(clickThrough: false);
            ProbeCase clickThrough = await RunCaseAsync(clickThrough: true);
            bool mappingMatches = baseline.MappingMatches && clickThrough.MappingMatches;
            bool compositionMatches = mappingMatches
                && baseline.Phases.Count == 2 && clickThrough.Phases.Count == 2;
            var comparisons = new List<object>();
            for (int i = 0; i < Math.Min(baseline.Phases.Count, clickThrough.Phases.Count); i++)
            {
                PhaseCapture before = baseline.Phases[i], after = clickThrough.Phases[i];
                bool dimensionsMatch = before.Width == after.Width && before.Height == after.Height
                    && before.Width == 640 && before.Height == 360;
                bool gpuInputsMatch = before.Gpu.AsSpan().SequenceEqual(after.Gpu);
                int maximumRgbError = 0, maximumAlphaError = 0, differentPixels = 0;
                if (dimensionsMatch)
                    for (int p = 0; p < before.Pixels.Length; p += 4)
                    {
                        bool different = false;
                        for (int c = 0; c < 4; c++)
                        {
                            int error = Math.Abs(before.Pixels[p + c] - after.Pixels[p + c]);
                            if (c == 3) maximumAlphaError = Math.Max(maximumAlphaError, error);
                            else maximumRgbError = Math.Max(maximumRgbError, error);
                            different |= error != 0;
                        }
                        if (different) differentPixels++;
                    }
                // Include alpha as well as RGB: equality here proves the new native styles
                // preserved what WGC observed, regardless of its representation of alpha.
                bool matches = dimensionsMatch && gpuInputsMatch && before.ElapsedMs == after.ElapsedMs
                    && maximumRgbError <= 2 && maximumAlphaError <= 2;
                compositionMatches &= matches;
                comparisons.Add(new
                {
                    ElapsedMs = before.ElapsedMs, DimensionsMatch = dimensionsMatch,
                    GpuInputsMatch = gpuInputsMatch, MaximumRgbError = maximumRgbError,
                    MaximumAlphaError = maximumAlphaError, DifferentPixels = differentPixels,
                    BaselineCentre = Centre(before.Pixels, before.Width, before.Height),
                    ClickThroughCentre = Centre(after.Pixels, after.Width, after.Height),
                    Matches = matches,
                });
            }
            return new
            {
                Supported = true,
                PixelCaptureAvailable = baseline.CaptureAvailable && clickThrough.CaptureAvailable,
                PixelMappingMatches = mappingMatches,
                ClickThroughStylesApplied = true,
                AlphaCompositionMatches = compositionMatches,
                CompositionComparison = "Same synthetic pixels and times, separate offscreen windows; all captured BGRA channels compared to the unmodified native-style baseline.",
                Baseline = baseline.Metadata, ClickThrough = clickThrough.Metadata,
                AlphaPhases = comparisons, UsedDesktopCapture = false, OffscreenOnly = true,
            };
        }

        private sealed class ProbeCase
        {
            internal object Metadata { get; init; } = new { };
            internal bool CaptureAvailable { get; init; }
            internal bool MappingMatches { get; init; }
            internal List<PhaseCapture> Phases { get; init; } = new();
        }

        private sealed record PhaseCapture(double ElapsedMs, int Width, int Height, byte[] Pixels, byte[] Gpu);

        private static string Centre(byte[] pixels, int width, int height)
            => width > 0 && height > 0 && pixels.Length == (long)width * height * 4
                ? Convert.ToHexString(pixels.AsSpan((height / 2 * width + width / 2) * 4, 4)) : "Unavailable";

        private static async Task<ProbeCase> RunCaseAsync(bool clickThrough)
        {
            const int width = 640, height = 360;
            Window? window = null;
            GpuRefreshRenderer? renderer = null;
            GpuRefreshPlayback? playback = null;
            IDirect3DDevice? captureDevice = null;
            GraphicsCaptureItem? item = null;
            Direct3D11CaptureFramePool? pool = null;
            GraphicsCaptureSession? session = null;
            Direct3D11CaptureFrame? frame = null;
            var frameGate = new object();
            Direct3D11CaptureFrame? latest = null;
            bool accepting = false;
            nint backgroundHwnd = 0;
            try
            {
                window = new Window();
                var host = new Canvas();
                window.Content = host;
                if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.SetBorderAndTitleBar(false, false);
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                window.SystemBackdrop = new RadialMenu.TransparentSystemBackdrop();
                var blur = new BlurBehind { Flags = 1, Enabled = true };
                GpuRefreshDevice.Check(DwmEnableBlurBehindWindow(hwnd, ref blur));
                if (!SetWindowSubclass(hwnd, ClearNativeBackground, new UIntPtr(37), UIntPtr.Zero))
                    throw new InvalidOperationException("Probe background subclass could not be installed.");
                backgroundHwnd = hwnd;
                // Match the actual overlay's borderless native HWND, including removal of
                // the invisible overlapped-window resize margins excluded by WGC.
                SetWindowLong(hwnd, -16, unchecked((int)0x82000000)); // POPUP | CLIPCHILDREN
                SetWindowLong(hwnd, -20, 0x08000080); // NOACTIVATE | TOOLWINDOW
                if (clickThrough) RefreshInputTransparency.Enable(hwnd);
                if (!SetWindowPos(hwnd, 0, -20000, -20000, width, height, 0x234))
                    throw new InvalidOperationException("Probe popup sizing failed.");
                window.AppWindow.Show(false);
                nint backgroundDc = GetDC(hwnd);
                if (backgroundDc != 0)
                {
                    GetClientRect(hwnd, out Rect eraseRect);
                    FillRect(backgroundDc, ref eraseRect, GetStockObject(4));
                    ReleaseDC(hwnd, backgroundDc);
                }
                await Task.Delay(250);
                if (!GetClientRect(hwnd, out Rect client))
                    throw new InvalidOperationException("Probe client size is unavailable.");
                if (client.Right != width || client.Bottom != height)
                {
                    if (!GetWindowRect(hwnd, out Rect current))
                        throw new InvalidOperationException("Probe outer size is unavailable.");
                    if (!SetWindowPos(hwnd, 0, -20000, -20000,
                        current.Right - current.Left + width - client.Right,
                        current.Bottom - current.Top + height - client.Bottom, 0x234))
                        throw new InvalidOperationException("Probe client correction failed.");
                    await Task.Delay(100);
                    if (!GetClientRect(hwnd, out client) || client.Right != width || client.Bottom != height)
                        throw new InvalidOperationException("Probe client dimensions do not match native output.");
                }
                double scale = host.XamlRoot?.RasterizationScale
                    ?? throw new InvalidOperationException("Probe host has no XamlRoot.");
                host.Width = width / scale; host.Height = height / scale;
                host.Measure(new Windows.Foundation.Size(host.Width, host.Height));
                host.Arrange(new Windows.Foundation.Rect(0, 0, host.Width, host.Height));
                host.UpdateLayout();
                await Task.Delay(100);
                if (!GetWindowRect(hwnd, out Rect bounds)
                    || bounds.Right >= -10000 || bounds.Bottom >= -10000)
                    throw new InvalidOperationException("Probe window is not fully outside the desktop.");
                byte[] source = new byte[width * height * 4];
                float[] depth = new float[width * height];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int pixel = y * width + x;
                    // Unique stepped coordinates and corner bands reveal any crop or zoom.
                    byte value = (byte)(20 + x * 180 / width + y * 40 / height);
                    if (x < 12 || x >= width - 12 || y < 12 || y >= height - 12)
                        value = (byte)((x / 8 + y / 8) % 2 == 0 ? 250 : 5);
                    source[pixel * 4] = source[pixel * 4 + 1] = source[pixel * 4 + 2] = value;
                    source[pixel * 4 + 3] = 255;
                    depth[pixel] = x / (float)(width - 1);
                }
                var reference = new SpectrumOverlayRefreshRenderer(width, height, source,
                    new RefreshDepthMap(width, height, depth), width / 2, height / 2);
                renderer = new GpuRefreshRenderer(new GpuRefreshScene(reference));
                playback = new GpuRefreshPlayback(renderer, host);
                playback.RenderFrame(0);
                renderer.Device.WaitIdle();
                byte[] expected = renderer.Device.ReadbackBgra(renderer.OutputTexture);
                await Task.Delay(250); // Let XAML/Composition commit before the capture session.
                captureDevice = CreateCaptureDevice(renderer.Device.NativeDevice);
                item = CreateItem(hwnd);
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                session = pool.CreateCaptureSession(item);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                    session.IsCursorCaptureEnabled = false;
                var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                accepting = true;
                int receivedFrames = 0;
                pool.FrameArrived += (sender, _) =>
                {
                    try
                    {
                        Direct3D11CaptureFrame? candidate = sender.TryGetNextFrame();
                        if (candidate == null) return;
                        lock (frameGate)
                        {
                            if (!accepting) { candidate.Dispose(); return; }
                            latest?.Dispose(); latest = candidate; receivedFrames++;
                        }
                        arrived.TrySetResult(true);
                    }
                    catch (Exception error) { arrived.TrySetException(error); }
                };
                session.StartCapture();
                // A bounded change and return wakes a later capture after the initial WGC
                // snapshot; only the final opaque coordinate pattern is sampled.
                await Task.Delay(100);
                playback.RenderFrame(600);
                await Task.Delay(100);
                playback.RenderFrame(0);
                Task winner = await Task.WhenAny(arrived.Task, Task.Delay(3500));
                if (winner != arrived.Task)
                {
                    arrived.TrySetCanceled();
                    lock (frameGate) { accepting = false; latest?.Dispose(); latest = null; }
                    return new ProbeCase { Metadata = new { Supported = true, PixelCaptureAvailable = false,
                        Reason = "No WGC frame for the fully offscreen synthetic window.",
                        Mapping = playback.GetMappingDiagnostic(), UsedDesktopCapture = false } };
                }
                await arrived.Task;
                await Task.Delay(250);
                lock (frameGate) { accepting = false; frame = latest; latest = null; }
                if (frame == null) throw new InvalidOperationException("WGC frame was unavailable after presentation.");
                byte[] actual = ReadCapturedSurface(renderer.Device, frame.Surface, out int actualWidth, out int actualHeight);
                var samples = new List<object>();
                int maximum = 0, sum = 0, count = 0, nonzeroInterior = 0;
                foreach ((int x, int y) in new (int, int)[]
                { (4,4), (width-5,4), (4,height-5), (width-5,height-5), (40,40),
                    (width/4,height/4), (width/2,height/2), (width*3/4,height*3/4), (width-30,height-30) })
                {
                    if (x >= actualWidth || y >= actualHeight) continue;
                    int nativeOffset = (y * width + x) * 4, capturedOffset = (y * actualWidth + x) * 4;
                    int error = Math.Abs(expected[nativeOffset] - actual[capturedOffset]);
                    maximum = Math.Max(maximum, error); sum += error; count++;
                    if (x > 12 && y > 12 && x < width - 12 && y < height - 12 && actual[capturedOffset] != 0)
                        nonzeroInterior++;
                    samples.Add(new { X = x, Y = y, Expected = expected[nativeOffset],
                        Actual = actual[capturedOffset], Error = error });
                }
                var alphaPhases = new List<object>();
                var phaseCaptures = new List<PhaseCapture>();
                foreach (double elapsed in new[] { renderer.DurationMs * .28, renderer.DurationMs })
                {
                    lock (frameGate) { accepting = true; latest?.Dispose(); latest = null; }
                    playback.RenderFrame(elapsed);
                    renderer.Device.WaitIdle();
                    byte[] expectedPhase = renderer.Device.ReadbackBgra(renderer.OutputTexture);
                    await Task.Delay(300);
                    Direct3D11CaptureFrame? capturedPhase;
                    lock (frameGate) { accepting = false; capturedPhase = latest; latest = null; }
                    if (capturedPhase == null)
                        throw new InvalidOperationException("No Composition frame after changing the layered refresh output.");
                    using (capturedPhase)
                    {
                        byte[] pixels = ReadCapturedSurface(renderer.Device, capturedPhase.Surface,
                            out int phaseWidth, out int phaseHeight);
                        int partialAlphaPixels = 0, clearAlphaPixels = 0;
                        bool dimensionsMatch = phaseWidth == width && phaseHeight == height;
                        if (dimensionsMatch)
                            for (int p = 0; p < expectedPhase.Length; p += 4)
                            {
                                if (expectedPhase[p + 3] == 0) clearAlphaPixels++;
                                else if (expectedPhase[p + 3] < 255) partialAlphaPixels++;
                            }
                        phaseCaptures.Add(new PhaseCapture(elapsed, phaseWidth, phaseHeight, pixels, expectedPhase));
                        alphaPhases.Add(new { ElapsedMs = elapsed, DimensionsMatch = dimensionsMatch,
                            PartialAlphaPixels = partialAlphaPixels, ClearAlphaPixels = clearAlphaPixels,
                            CapturedCentre = Centre(pixels, phaseWidth, phaseHeight),
                            GpuCentre = Centre(expectedPhase, width, height) });
                    }
                }
                bool mappingMatches = nonzeroInterior >= 3 && count == 9 && maximum <= 2
                    && actualWidth == width && actualHeight == height;
                return new ProbeCase
                {
                    CaptureAvailable = nonzeroInterior >= 3,
                    MappingMatches = mappingMatches,
                    Phases = phaseCaptures,
                    Metadata = new { Supported = true, PixelCaptureAvailable = nonzeroInterior >= 3,
                    Reason = nonzeroInterior >= 3 ? "Captured final synthetic Composition frame."
                        : "WGC supplied a blank offscreen Composition interior; pixel mapping is unverified.",
                    Width = width, Height = height, CaptureWidth = actualWidth, CaptureHeight = actualHeight,
                    ContentWidth = frame.ContentSize.Width, ContentHeight = frame.ContentSize.Height,
                    ClientWidth = client.Right, ClientHeight = client.Bottom, ReceivedFrames = receivedFrames,
                    Mapping = playback.GetMappingDiagnostic(), Samples = samples,
                    MaximumSampleError = maximum, MeanSampleError = count == 0 ? 0 : sum / (double)count,
                    PixelMappingMatches = mappingMatches,
                    ClickThroughStylesApplied = clickThrough,
                    AlphaPhases = alphaPhases,
                    UsedDesktopCapture = false, OffscreenOnly = true },
                };
            }
            catch (Exception error)
            {
                return new ProbeCase { Metadata = new { Supported = true, PixelCaptureAvailable = false,
                    Error = error.ToString(), UsedDesktopCapture = false } };
            }
            finally
            {
                lock (frameGate) { accepting = false; latest?.Dispose(); latest = null; }
                frame?.Dispose(); session?.Dispose(); pool?.Dispose();
                if (item is WinRT.IWinRTObject itemNative) itemNative.NativeObject.Dispose();
                if (captureDevice is IDisposable disposableDevice) disposableDevice.Dispose();
                else if (captureDevice is WinRT.IWinRTObject deviceNative) deviceNative.NativeObject.Dispose();
                if (backgroundHwnd != 0) RemoveWindowSubclass(backgroundHwnd, ClearNativeBackground, new UIntPtr(37));
                playback?.Dispose(); renderer?.Dispose(); window?.Close();
            }
        }

        private static readonly SubclassProc ClearNativeBackground = (hwnd, message, wParam, lParam, id, data) =>
        {
            if (message == 0x14 && GetClientRect(hwnd, out Rect rect))
            {
                FillRect(wParam, ref rect, GetStockObject(4));
                return 1;
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };

        private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, UIntPtr id, UIntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct BlurBehind
        { public uint Flags; [MarshalAs(UnmanagedType.Bool)] public bool Enabled; public nint Region; [MarshalAs(UnmanagedType.Bool)] public bool Transition; }
        [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, UIntPtr id);
        [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
        [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);

        private static unsafe GraphicsCaptureItem CreateItem(nint hwnd)
        {
            const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
            nint text = 0, factory = 0, result = 0;
            Guid interopId = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Guid itemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            try
            {
                GpuRefreshDevice.Check(WindowsCreateString(name, name.Length, &text));
                GpuRefreshDevice.Check(RoGetActivationFactory(text, &interopId, &factory));
                GpuRefreshDevice.Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)
                    GpuRefreshDevice.V(factory, 3))(factory, hwnd, &itemId, &result));
                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(result);
            }
            finally
            {
                GpuRefreshDevice.Release(ref result); GpuRefreshDevice.Release(ref factory);
                if (text != 0) WindowsDeleteString(text);
            }
        }

        private static unsafe IDirect3DDevice CreateCaptureDevice(nint device)
        {
            nint dxgi = GpuRefreshDevice.Query(device, new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"));
            nint inspectable = 0;
            try
            {
                GpuRefreshDevice.Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable));
                return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally { GpuRefreshDevice.Release(ref inspectable); GpuRefreshDevice.Release(ref dxgi); }
        }

        private static unsafe byte[] ReadCapturedSurface(GpuRefreshDevice device, IDirect3DSurface surface,
            out int width, out int height)
        {
            nint native = ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr;
            nint access = GpuRefreshDevice.Query(native, new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"));
            nint texture = 0, staging = 0;
            try
            {
                Guid iid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                GpuRefreshDevice.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)
                    GpuRefreshDevice.V(access, 3))(access, &iid, &texture));
                TextureDescription desc = default;
                ((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void>)
                    GpuRefreshDevice.V(texture, 10))(texture, &desc);
                width = checked((int)desc.Width); height = checked((int)desc.Height);
                if (desc.Format != 87) throw new InvalidOperationException("Probe WGC surface is not BGRA8.");
                desc.Usage = 3; desc.BindFlags = 0; desc.CpuAccessFlags = 0x20000; desc.MiscFlags = 0;
                GpuRefreshDevice.Check(((delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)
                    GpuRefreshDevice.V(device.NativeDevice, 5))(device.NativeDevice, &desc, 0, &staging));
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)
                    GpuRefreshDevice.V(device.NativeContext, 47))(device.NativeContext, staging, texture);
                MappedResource mapped = default;
                GpuRefreshDevice.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedResource*, int>)
                    GpuRefreshDevice.V(device.NativeContext, 14))(device.NativeContext, staging, 0, 1, 0, &mapped));
                try
                {
                    byte[] pixels = new byte[checked(width * height * 4)];
                    fixed (byte* destination = pixels)
                        for (int y = 0; y < height; y++)
                            Buffer.MemoryCopy((byte*)mapped.Memory + y * mapped.RowPitch,
                                destination + y * width * 4, width * 4, width * 4);
                    return pixels;
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)
                        GpuRefreshDevice.V(device.NativeContext, 15))(device.NativeContext, staging, 0);
                }
            }
            finally
            {
                GpuRefreshDevice.Release(ref staging); GpuRefreshDevice.Release(ref texture);
                GpuRefreshDevice.Release(ref access);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct TextureDescription
        { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct MappedResource { public nint Memory; public uint RowPitch, DepthPitch; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern unsafe int WindowsCreateString(string source, int length, nint* value);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
        [DllImport("combase.dll")] private static extern unsafe int RoGetActivationFactory(nint name, Guid* iid, nint* factory);
        [DllImport("d3d11.dll")] private static extern unsafe int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, nint* device);
    }
}
