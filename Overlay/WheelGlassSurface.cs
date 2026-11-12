using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Composition;
using Colors = Microsoft.UI.Colors;
using CompositionTarget = Microsoft.UI.Xaml.Media.CompositionTarget;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// A live desktop backdrop restricted to the wheel's circular sheet. It uses the OS compositor
    /// already owned by TransparentSystemBackdrop; the WinUI compositor cannot create a host
    /// backdrop brush, and in-app AcrylicBrush would only blur this window's own XAML content.
    /// </summary>
    internal sealed class WheelGlassSurface : IDisposable
    {
        // 17 enables CreateHostBackdropBrush on Win32 windows (Windows 11). Attribute 38 instead
        // assigns a material to the whole HWND, which would also affect the full-screen refresh.
        private const int DwmUseHostBackdropBrush = 17;
        internal const double FrostedMaterialOpacity = 0.60;
        internal const float FrostedBlurAmount = 24;
        internal const byte DarkTintAlpha = 52;
        internal const byte LightTintAlpha = 48;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
            ref int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

        private readonly Border _host;
        private readonly IntPtr _hwnd;
        private readonly bool _dark;
        private readonly double _materialOpacity;
        private readonly Action<CompositionBrush?> _setBackdropBrush;
        private readonly CompositionBackdropBrush _desktopBrush;
        private readonly CompositionColorBrush _maskFill;
        private readonly CompositionEllipseGeometry _ellipse;
        private readonly CompositionSpriteShape _ellipseShape;
        private readonly ShapeVisual _maskVisual;
        private readonly CompositionVisualSurface _maskSurface;
        private readonly CompositionSurfaceBrush _maskSurfaceBrush;
        private readonly CompositionEffectBrush _glassBrush;
        private readonly List<IDisposable> _ownedResources = new();
        private bool _disposed;
        private SolidColorBrush? _tintBrush;
        private Color _tintColor;
        private Color _lastMaskColor = Colors.Transparent;
        private Vector2 _lastClientSize;

        // Kept in memory for the hidden probe; the application still writes no activity/error log.
        internal static string? LastAttachFailure { get; private set; }
        internal bool IsConnected { get; private set; }
        internal double MaterialOpacity => _materialOpacity;
        internal float BlurAmount => _glassBrush.Properties.TryGetScalar("WheelFrost.BlurAmount", out float value)
            == CompositionGetValueStatus.Succeeded ? value : 0;
        internal CompositionBrush Brush => _glassBrush;
        internal GlassMaskDiagnostics Diagnostics { get; private set; }

        internal readonly record struct GlassMaskDiagnostics(Vector2 ClientSize,
            Vector2 Center, Vector2 Radius, double DpiScale, double Opacity,
            double MaterialOpacity, double MaskOpacity, bool BrushChainConnected,
            string? FailureReason);

        /// <summary>
        /// Installs a masked live desktop brush without disconnecting the transparent system
        /// backdrop. Returning null leaves the ordinary tint in place on unsupported systems.
        /// </summary>
        internal static WheelGlassSurface? Attach(Border host, IntPtr hwnd, bool dark,
            Compositor compositor, Action<CompositionBrush?> setBackdropBrush, double? materialOpacity = null)
        {
            LastAttachFailure = null;
            // Keep a readable fallback until the real material is connected. Tint belongs only
            // to the sheet; the independent XAML dividers retain their own opacity and contrast.
            ApplyTint(host, dark, materialAvailable: false);

            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                LastAttachFailure = "A valid HWND and Windows 11 host backdrop support are required.";
                return null;
            }

            int enabled = 1;
            int result = DwmSetWindowAttribute(hwnd, DwmUseHostBackdropBrush, ref enabled, sizeof(int));
            if (result < 0)
            {
                LastAttachFailure = $"DWMWA_USE_HOSTBACKDROPBRUSH failed: 0x{result:X8}.";
                return null;
            }

            WheelGlassSurface? surface = null;
            try
            {
                surface = new WheelGlassSurface(host, hwnd, dark, compositor, setBackdropBrush,
                    materialOpacity ?? FrostedMaterialOpacity);
                surface.UpdateMask();
                setBackdropBrush(surface._glassBrush);
                surface.IsConnected = true;
                ApplyTint(host, dark, materialAvailable: true);
                surface._tintBrush = host.Background as SolidColorBrush;
                surface._tintColor = surface._tintBrush?.Color ?? Colors.Transparent;
                CompositionTarget.Rendering += surface.OnRendering;
                host.Unloaded += surface.OnHostUnloaded;
                return surface;
            }
            catch (Exception exception)
            {
                // Device/material support can change while the app is running. Preserve the
                // transparent window and the readable tint instead of breaking the wheel.
                LastAttachFailure = $"{exception.GetType().Name}: {exception.Message}";
                surface?.Dispose();
                ApplyTint(host, dark, materialAvailable: false);
                return null;
            }
        }

        [SupportedOSPlatform("windows10.0.18362.0")]
        private WheelGlassSurface(Border host, IntPtr hwnd, bool dark, Compositor compositor,
            Action<CompositionBrush?> setBackdropBrush, double materialOpacity)
        {
            _host = host;
            _hwnd = hwnd;
            _dark = dark;
            _materialOpacity = Math.Clamp(materialOpacity, 0, 1);
            _setBackdropBrush = setBackdropBrush;
            Diagnostics = Diagnostics with { MaterialOpacity = _materialOpacity };
            try
            {
                _desktopBrush = Own(compositor.CreateHostBackdropBrush());
                _maskFill = Own(compositor.CreateColorBrush(Colors.Transparent));
                _ellipse = Own(compositor.CreateEllipseGeometry());
                _ellipseShape = Own(compositor.CreateSpriteShape(_ellipse));
                _ellipseShape.FillBrush = _maskFill;
                _maskVisual = Own(compositor.CreateShapeVisual());
                _maskVisual.Shapes.Add(_ellipseShape);
                _maskSurface = Own(compositor.CreateVisualSurface());
                _maskSurface.SourceVisual = _maskVisual;
                _maskSurfaceBrush = Own(compositor.CreateSurfaceBrush(_maskSurface));
                // The source spans the complete native client area. Fill maps that normalized
                // mask to the system-backdrop paint bounds even when XAML and the OS target use
                // different DPI units; None would assume identical pixel units.
                _maskSurfaceBrush.Stretch = CompositionStretch.Fill;
                _maskSurfaceBrush.HorizontalAlignmentRatio = 0;
                _maskSurfaceBrush.VerticalAlignmentRatio = 0;
                // A CompositionMaskBrush rejects a backdrop brush as its source. Express both
                // blur and masking as one GPU effect graph instead; named effect sources accept
                // the real host backdrop and the live circular mask without capturing pixels.
                var blurEffect = Own(new GaussianBlurEffect
                {
                    Name = "WheelFrost",
                    Source = Own(new ArithmeticCompositeEffect
                    {
                        Name = "WheelPigment",
                        Source1 = new CompositionEffectSourceParameter("desktop"),
                        Source2 = Own(new ColorMatrixEffect
                        {
                            Source = new CompositionEffectSourceParameter("desktop"),
                            ColorMatrix = new Matrix5x4
                            {
                                M11 = .30f, M12 = .30f, M13 = .30f,
                                M21 = .59f, M22 = .59f, M23 = .59f,
                                M31 = .11f, M32 = .11f, M33 = .11f, M44 = 1,
                            },
                            ClampOutput = true,
                        }),
                        MultiplyAmount = 0, Source1Amount = 1, Source2Amount = 0,
                        Offset = 0, ClampOutput = true,
                    }),
                    BlurAmount = FrostedBlurAmount,
                    BorderMode = EffectBorderMode.Hard,
                });
                var maskEffect = Own(new AlphaMaskEffect
                {
                    Source = blurEffect,
                    AlphaMask = new CompositionEffectSourceParameter("mask"),
                });
                var effectFactory = Own(compositor.CreateEffectFactory(maskEffect,
                    new[] { "WheelFrost.BlurAmount", "WheelPigment.Source1Amount", "WheelPigment.Source2Amount" }));
                _glassBrush = Own(effectFactory.CreateBrush());
                _glassBrush.Properties.InsertScalar("WheelFrost.BlurAmount", FrostedBlurAmount);
                ApplySaturation(1);
                _glassBrush.SetSourceParameter("desktop", _desktopBrush);
                _glassBrush.SetSourceParameter("mask", _maskSurfaceBrush);
            }
            catch
            {
                // Attach cannot dispose an instance whose constructor never returned. Every
                // successful allocation is registered immediately, including partial creation.
                DisposeOwnedResources();
                throw;
            }
        }

        private T Own<T>(T resource) where T : IDisposable
        {
            _ownedResources.Add(resource);
            return resource;
        }

        /// <summary>
        /// Grade only the sampled material. HostBackdrop may omit the capture-excluded grey
        /// desktop window; applying its saturation explicitly keeps the glass grey either way.
        /// Independent XAML icons, selection and dividers are outside this effect graph.
        /// </summary>
        internal void ApplySaturation(float saturation)
        {
            if (_disposed) return;
            if (!float.IsFinite(saturation)) throw new ArgumentOutOfRangeException(nameof(saturation));
            saturation = Math.Clamp(saturation, 0, 1);
            _glassBrush.Properties.InsertScalar("WheelPigment.Source1Amount", saturation);
            _glassBrush.Properties.InsertScalar("WheelPigment.Source2Amount", 1 - saturation);
            if (_tintBrush != null)
            {
                float grey = .30f * _tintColor.R + .59f * _tintColor.G + .11f * _tintColor.B;
                byte Channel(byte color) => (byte)Math.Round(grey + (color - grey) * saturation);
                _tintBrush.Color = Color.FromArgb(_tintColor.A,
                    Channel(_tintColor.R), Channel(_tintColor.G), Channel(_tintColor.B));
            }
        }

        private static void ApplyTint(Border host, bool dark, bool materialAvailable)
        {
            byte alpha = materialAvailable
                ? dark ? DarkTintAlpha : LightTintAlpha
                : dark ? (byte)142 : (byte)120;
            host.Background = new SolidColorBrush(dark
                ? Color.FromArgb(alpha, 21, 27, 37)
                : Color.FromArgb(alpha, 242, 246, 251));
        }

        private void OnRendering(object? sender, object args)
        {
            if (_disposed)
                return;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                return;
            try { UpdateMask(); }
            catch (Exception exception)
            {
                Diagnostics = Diagnostics with
                {
                    FailureReason = $"{exception.GetType().Name}: {exception.Message}"
                };
                ApplyTint(_host, _dark, materialAvailable: false);
                Dispose();
            }
        }

        internal void RefreshDiagnostics()
        {
            if (!_disposed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                UpdateMask();
        }

        [SupportedOSPlatform("windows10.0.18362.0")]
        private void UpdateMask()
        {
            if (_host.XamlRoot?.Content is not UIElement root ||
                !GetClientRect(_hwnd, out var client))
            {
                SetMaskColor(Colors.Transparent);
                Diagnostics = Diagnostics with { Opacity = 0, MaskOpacity = 0, FailureReason = "Waiting for XamlRoot/client area." };
                return;
            }

            var clientSize = new Vector2(Math.Max(1, client.Right - client.Left),
                Math.Max(1, client.Bottom - client.Top));
            if (clientSize != _lastClientSize)
            {
                _maskVisual.Size = clientSize;
                _maskSurface.SourceSize = clientSize;
                _lastClientSize = clientSize;
            }

            double width = _host.ActualWidth > 0 ? _host.ActualWidth : _host.Width;
            double height = _host.ActualHeight > 0 ? _host.ActualHeight : _host.Height;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            {
                SetMaskColor(Colors.Transparent);
                Diagnostics = Diagnostics with { Opacity = 0, MaskOpacity = 0, FailureReason = "Waiting for the glass sheet layout." };
                return;
            }

            double opacity = 1;
            DependencyObject? ancestor = _host;
            while (ancestor != null)
            {
                if (ancestor is UIElement element)
                {
                    if (element.Visibility != Visibility.Visible)
                    {
                        opacity = 0;
                        break;
                    }
                    opacity *= element.Opacity;
                }
                ancestor = VisualTreeHelper.GetParent(ancestor);
            }

            // SystemBackdrop belongs to the OS window, so its mask uses physical client pixels.
            // Three transformed points follow the XAML scale/translation without inflating a
            // circle's radius during Sweep's rotation as TransformBounds would do.
            double dpiScale = _host.XamlRoot.RasterizationScale;
            var transform = _host.TransformToVisual(root);
            Point center = transform.TransformPoint(new Point(width / 2, height / 2));
            Point edgeX = transform.TransformPoint(new Point(width, height / 2));
            Point edgeY = transform.TransformPoint(new Point(width / 2, height));
            var maskCenter = new Vector2((float)(center.X * dpiScale), (float)(center.Y * dpiScale));
            var maskRadius = new Vector2((float)(Distance(center, edgeX) * dpiScale),
                (float)(Distance(center, edgeY) * dpiScale));
            if (Diagnostics.Center != maskCenter) _ellipse.Center = maskCenter;
            if (Diagnostics.Radius != maskRadius) _ellipse.Radius = maskRadius;
            // Encode alpha in the shape itself: a VisualSurface may ignore transform/opacity on
            // its source root, whereas the rendered fill alpha is part of the captured content.
            // Material coverage is independent of the opening fade: the settled sheet mixes 60%
            // frosted desktop with the live desktop beneath, while dividers remain untouched.
            double ancestorOpacity = Math.Clamp(opacity, 0, 1);
            double maskOpacity = ancestorOpacity * _materialOpacity;
            var maskColor = Color.FromArgb((byte)Math.Round(maskOpacity * 255), 255, 255, 255);
            SetMaskColor(maskColor);
            Diagnostics = new GlassMaskDiagnostics(clientSize, _ellipse.Center, _ellipse.Radius,
                dpiScale, ancestorOpacity, _materialOpacity, maskOpacity,
                _glassBrush.GetSourceParameter("desktop") == _desktopBrush &&
                _glassBrush.GetSourceParameter("mask") == _maskSurfaceBrush,
                null);
        }

        private static double Distance(Point a, Point b)
        {
            double x = a.X - b.X;
            double y = a.Y - b.Y;
            return Math.Sqrt(x * x + y * y);
        }

        private void SetMaskColor(Color color)
        {
            if (_lastMaskColor == color) return;
            _maskFill.Color = color;
            _lastMaskColor = color;
        }

        private void OnHostUnloaded(object sender, RoutedEventArgs args) => Dispose();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            IsConnected = false;
            CompositionTarget.Rendering -= OnRendering;
            _host.Unloaded -= OnHostUnloaded;
            // Restore the existing transparent color brush first; never switch SystemBackdrop or
            // dispose its compositor. The full-screen refresh continues to use its original path.
            try { _setBackdropBrush(null); } catch (Exception) { }
            DisposeOwnedResources();
        }

        private void DisposeOwnedResources()
        {
            for (int index = _ownedResources.Count - 1; index >= 0; index--)
            {
                try { _ownedResources[index].Dispose(); } catch (Exception) { }
            }
            _ownedResources.Clear();
        }
    }
}
