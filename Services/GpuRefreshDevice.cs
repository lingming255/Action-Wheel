using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Small D3D11 owner for the refresh passes. Resources stay on the GPU until Composition
    /// presents them; readback and completion waits exist only for offscreen diagnostics.
    /// The immediate context has exactly one caller at a time (prepare worker, then UI thread).
    /// </summary>
    internal sealed unsafe class GpuRefreshDevice : IDisposable
    {
        private static readonly ConcurrentDictionary<(string Source, string Entry, string Profile), byte[]> Bytecode = new();
        private static readonly object PoolGate = new();
        private static Backend? _idleBackend;
        private static bool _poolShutdown;
        private nint _device, _context, _vertexShader, _sampler, _rasterizer, _constants, _swapChain;
        private readonly List<IDisposable> _resources = new();
        private readonly bool _allowReuse;
        private bool _backendReady;
        private bool _disposed;
        private double _swapChainScale;
        internal nint SwapChain => _swapChain;
        internal nint NativeDevice => _device;
        internal nint NativeContext => _context;
        internal double SwapChainScale => _swapChainScale;
        internal bool ReusedBackend { get; private set; }
        internal string AdapterName { get; private set; } = "";
        internal uint VendorId { get; private set; }
        internal uint FeatureLevel { get; private set; }
        internal bool IsHardware => true;

        private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid Factory2Id = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        internal sealed class Texture : IDisposable
        {
            internal nint Resource, View, Target;
            internal int Format { get; }
            public int Width { get; }
            public int Height { get; }
            internal Texture(nint resource, nint view, nint target, int width, int height, int format)
            { Resource = resource; View = view; Target = target; Width = width; Height = height; Format = format; }
            public void Dispose() { Release(ref Target); Release(ref View); Release(ref Resource); }
        }

        internal sealed class Shader : IDisposable
        {
            internal nint Pointer;
            internal Shader(nint pointer) => Pointer = pointer;
            public void Dispose() => Release(ref Pointer);
        }

        public GpuRefreshDevice(bool allowReuse = true)
        {
            _allowReuse = allowReuse;
            if (allowReuse)
            {
                Backend? cached;
                lock (PoolGate)
                {
                    cached = _poolShutdown ? null : _idleBackend;
                    _idleBackend = null;
                }
                if (cached != null)
                {
                    if (cached.IsHealthy)
                    {
                        _device = cached.Device; _context = cached.Context;
                        _vertexShader = cached.VertexShader; _sampler = cached.Sampler;
                        _rasterizer = cached.Rasterizer; _constants = cached.Constants;
                        AdapterName = cached.AdapterName; VendorId = cached.VendorId; FeatureLevel = cached.FeatureLevel;
                        _backendReady = true; ReusedBackend = true;
                        return;
                    }
                    cached.Dispose();
                }
            }
            try
            {
                nint device = 0, context = 0;
                uint level = 0;
                // Hardware only: WARP would disguise a software fallback as GPU acceleration.
                Check(D3D11CreateDevice(0, 1, 0, 0x20, null, 0, 7, &device, &level, &context));
                _device = device; _context = context; FeatureLevel = level;
                if (level < 0xa000) throw new NotSupportedException("Refresh shaders require Direct3D feature level 10.0.");
                ReadAdapter();
                byte[] vertices = Compile("float4 VS(uint id:SV_VertexID):SV_Position { return float4(id==2?3:-1,id==1?3:-1,0,1); }", "VS", "vs_4_0");
                fixed (byte* bytes = vertices)
                {
                    nint vertex = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, void*, nuint, nint, nint*, int>)V(_device, 12))(_device, bytes, (nuint)vertices.Length, 0, &vertex));
                    _vertexShader = vertex;
                }
                SamplerDesc sampler = new() { Filter = 0x15, AddressU = 3, AddressV = 3, AddressW = 3, MaxLod = float.MaxValue, MaxAnisotropy = 1, ComparisonFunc = 1 };
                nint state = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)V(_device, 23))(_device, &sampler, &state));
                _sampler = state;
                RasterizerDesc rasterizer = new() { FillMode = 3, CullMode = 1, DepthClipEnable = 1 };
                state = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, RasterizerDesc*, nint*, int>)V(_device, 22))(_device, &rasterizer, &state));
                _rasterizer = state;
                BufferDesc buffer = new() { ByteWidth = 512, BindFlags = 4 };
                nint constantBuffer = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(_device, 3))(_device, &buffer, null, &constantBuffer));
                _constants = constantBuffer;
                _backendReady = true;
            }
            catch { Dispose(); throw; }
        }

        /// <summary>
        /// Stops caching permanently during app shutdown. Active facades keep their exclusive
        /// backend until disposal; they can no longer repopulate the process-wide idle slot.
        /// </summary>
        internal static void ShutdownPool()
        {
            Backend? idle;
            lock (PoolGate)
            {
                _poolShutdown = true;
                idle = _idleBackend; _idleBackend = null;
            }
            idle?.Dispose();
        }

        private sealed class Backend : IDisposable
        {
            internal nint Device, Context, VertexShader, Sampler, Rasterizer, Constants;
            internal string AdapterName = "";
            internal uint VendorId, FeatureLevel;
            internal bool IsHealthy => Device != 0
                && ((delegate* unmanaged[Stdcall]<nint, int>)V(Device, 39))(Device) == 0;

            public void Dispose()
            {
                Release(ref Constants); Release(ref Rasterizer); Release(ref Sampler);
                Release(ref VertexShader); Release(ref Context); Release(ref Device);
            }
        }

        private void ReadAdapter()
        {
            nint dxgi = Query(_device, DxgiDeviceId), adapter = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)V(dxgi, 7))(dxgi, &adapter));
                AdapterDesc description = default;
                Check(((delegate* unmanaged[Stdcall]<nint, AdapterDesc*, int>)V(adapter, 8))(adapter, &description));
                AdapterName = new string(description.Description);
                VendorId = description.VendorId;
            }
            finally { Release(ref adapter); Release(ref dxgi); }
        }

        public Texture CreateFloatTexture(int width, int height, Vector4[]? pixels = null)
        {
            if (pixels != null && pixels.Length != checked(width * height)) throw new ArgumentException("Float texture input size mismatch.");
            fixed (Vector4* data = pixels) return CreateTexture(width, height, 2, data, checked(width * 16), true);
        }

        public Texture CreateBgraTexture(int width, int height, byte[] pixels)
        {
            if (pixels.Length != checked(width * height * 4)) throw new ArgumentException("BGRA texture input size mismatch.");
            fixed (byte* data = pixels) return CreateTexture(width, height, 87, data, checked(width * 4), false);
        }

        public Texture CreateBgraTarget(int width, int height) => CreateTexture(width, height, 87, null, width * 4, true);

        private Texture CreateTexture(int width, int height, int format, void* pixels, int pitch, bool target)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            TextureDesc description = new() { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = format, SampleCount = 1, BindFlags = (uint)(8 | (target ? 32 : 0)) };
            SubresourceData initial = new() { Memory = (nint)pixels, RowPitch = (uint)pitch };
            nint resource = 0, view = 0, renderTarget = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, SubresourceData*, nint*, int>)V(_device, 5))(_device, &description, pixels == null ? null : &initial, &resource));
                Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(_device, 7))(_device, resource, null, &view));
                if (target) Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(_device, 9))(_device, resource, null, &renderTarget));
                var texture = new Texture(resource, view, renderTarget, width, height, format);
                _resources.Add(texture);
                return texture;
            }
            catch { Release(ref renderTarget); Release(ref view); Release(ref resource); throw; }
        }

        public void UpdateFloatTexture(Texture texture, Vector4[] pixels)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (texture.Format != 2 || pixels.Length != checked(texture.Width * texture.Height)) throw new ArgumentException("Float update input mismatch.");
            fixed (Vector4* data = pixels)
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)V(_context, 48))(_context, texture.Resource, 0, null, data, (uint)texture.Width * 16, 0);
        }

        public Shader CreateShader(string source, string entry)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] code = Compile(source, entry, "ps_4_0");
            fixed (byte* bytes = code)
            {
                nint shader = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, void*, nuint, nint, nint*, int>)V(_device, 15))(_device, bytes, (nuint)code.Length, 0, &shader));
                var result = new Shader(shader);
                _resources.Add(result);
                return result;
            }
        }

        public void Draw(Shader shader, Texture target, ReadOnlySpan<Texture> inputs, ReadOnlySpan<Vector4> constants)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (inputs.Length > 16 || constants.Length > 32 || target.Target == 0) throw new ArgumentException("Unsupported GPU pass bindings.");
            nint* views = stackalloc nint[16];
            for (int index = 0; index < 16; index++) views[index] = index < inputs.Length ? inputs[index].View : 0;
            // Explicitly unbind previous inputs before binding a render target reused in a later pass.
            nint* empty = stackalloc nint[16];
            for (int index = 0; index < 16; index++) empty[index] = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(_context, 8))(_context, 0, 16, empty);
            nint output = target.Target;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(_context, 33))(_context, 1, &output, 0);
            Viewport viewport = new() { Width = target.Width, Height = target.Height, MaxDepth = 1 };
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(_context, 44))(_context, 1, &viewport);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(_context, 43))(_context, _rasterizer);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(_context, 17))(_context, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)V(_context, 24))(_context, 4);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(_context, 11))(_context, _vertexShader, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(_context, 9))(_context, shader.Pointer, null, 0);
            nint sampler = _sampler, constantBuffer = _constants;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(_context, 10))(_context, 0, 1, &sampler);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(_context, 8))(_context, 0, (uint)inputs.Length, views);
            Vector4* values = stackalloc Vector4[32];
            new Span<Vector4>(values, 32).Clear();
            constants.CopyTo(new Span<Vector4>(values, 32));
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)V(_context, 48))(_context, _constants, 0, null, values, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(_context, 16))(_context, 0, 1, &constantBuffer);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(_context, 13))(_context, 3, 0);
        }

        /// <summary>Creates a premultiplied Composition swapchain; never opens a visible window.</summary>
        public void CreateSwapChain(int width, int height)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_swapChain != 0) throw new InvalidOperationException("Swapchain already created.");
            nint dxgi = Query(_device, DxgiDeviceId), adapter = 0, factory = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)V(dxgi, 7))(dxgi, &adapter));
                Guid iid = Factory2Id;
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)V(adapter, 6))(adapter, &iid, &factory));
                SwapChainDesc description = new() { Width = (uint)width, Height = (uint)height, Format = 87,
                    SampleCount = 1, BufferUsage = 0x20, BufferCount = 2, SwapEffect = 3, AlphaMode = 1 };
                nint swapchain = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, SwapChainDesc*, nint, nint*, int>)V(factory, 24))(factory, _device, &description, 0, &swapchain));
                _swapChain = swapchain;
            }
            finally { Release(ref factory); Release(ref adapter); Release(ref dxgi); }
        }

        internal bool SetSwapChainScale(double scale)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
            if (_swapChainScale == scale) return false;
            nint swapChain2 = Query(_swapChain, new Guid("a8be2ac4-199f-4946-b331-79599fb98de7"));
            try
            {
                // Raw composition swapchains do not apply SurfaceBrush.Fill to their
                // pixels. Match Win2D's physical-pixel -> DIP matrix explicitly.
                var matrix = Matrix3x2.CreateScale((float)(1 / scale));
                Check(((delegate* unmanaged[Stdcall]<nint, Matrix3x2*, int>)V(swapChain2, 34))(
                    swapChain2, &matrix));
                _swapChainScale = scale;
                return true;
            }
            finally { Release(ref swapChain2); }
        }

        public bool Present(Texture output)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_swapChain == 0 || output.Format != 87) throw new InvalidOperationException("A BGRA output and Composition swapchain are required.");
            nint buffer = 0;
            try
            {
                Guid iid = Texture2DId;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)V(_swapChain, 9))(_swapChain, 0, &iid, &buffer));
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(_context, 33))(_context, 0, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(_context, 47))(_context, buffer, output.Resource);
                // Composition is synchronized by the XAML rendering callback. Do not block the
                // UI thread on vblank or on a full queue; discard that presentation if busy.
                int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)V(_swapChain, 8))(_swapChain, 0, 8);
                if (result == unchecked((int)0x887A000A)) return false;
                Check(result);
                return true;
            }
            finally { Release(ref buffer); }
        }

        /// <summary>Diagnostic only: stages an output back to CPU. Playback never calls this.</summary>
        public byte[] ReadBgra(Texture source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (source.Format != 87) throw new ArgumentException("ReadBgra requires a BGRA texture.");
            nint staging = CreateStaging(source);
            try
            {
                MappedResource mapped = default;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedResource*, int>)V(_context, 14))(_context, staging, 0, 1, 0, &mapped));
                try
                {
                    byte[] result = new byte[checked(source.Width * source.Height * 4)];
                    fixed (byte* destination = result)
                        for (int y = 0; y < source.Height; y++) Buffer.MemoryCopy((byte*)mapped.Memory + y * mapped.RowPitch, destination + y * source.Width * 4, source.Width * 4, source.Width * 4);
                    return result;
                }
                finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(_context, 15))(_context, staging, 0); }
            }
            finally { Release(ref staging); }
        }

        private nint CreateStaging(Texture source)
        {
            TextureDesc description = new() { Width = (uint)source.Width, Height = (uint)source.Height,
                MipLevels = 1, ArraySize = 1, Format = source.Format, SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000 };
            nint staging = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, SubresourceData*, nint*, int>)V(_device, 5))(_device, &description, null, &staging));
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(_context, 47))(_context, staging, source.Resource);
            return staging;
        }

        public void Finish()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            nint query = NewQuery(0);
            try
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(_context, 28))(_context, query);
                ((delegate* unmanaged[Stdcall]<nint, void>)V(_context, 111))(_context);
                uint finished = 0;
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    int result = ((delegate* unmanaged[Stdcall]<nint, nint, void*, uint, uint, int>)V(_context, 29))(_context, query, &finished, 4, 0);
                    Check(result);
                    if (result == 0) return;
                    if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("GPU completion timed out.");
                    Thread.Yield();
                }
            }
            finally { Release(ref query); }
        }

        public void WaitIdle() => Finish();

        public byte[] ReadbackBgra(Texture source) => ReadBgra(source);

        /// <summary>Diagnostic GPU time for submitted passes, without counting CPU readback.</summary>
        public double MeasureGpuMilliseconds(Action render)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            nint disjoint = NewQuery(3), begin = 0, end = 0;
            try
            {
                begin = NewQuery(2); end = NewQuery(2);
                ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(_context, 27))(_context, disjoint);
                ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(_context, 28))(_context, begin);
                render();
                ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(_context, 28))(_context, end);
                ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(_context, 28))(_context, disjoint);
                Finish();
                TimestampDisjoint result = default;
                ulong first = 0, last = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, uint, uint, int>)V(_context, 29))(_context, disjoint, &result, (uint)sizeof(TimestampDisjoint), 0));
                Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, uint, uint, int>)V(_context, 29))(_context, begin, &first, 8, 0));
                Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, uint, uint, int>)V(_context, 29))(_context, end, &last, 8, 0));
                return result.Disjoint != 0 || result.Frequency == 0 ? double.NaN : (last - first) * 1000.0 / result.Frequency;
            }
            finally { Release(ref end); Release(ref begin); Release(ref disjoint); }
        }

        private nint NewQuery(uint type)
        {
            QueryDesc description = new() { Query = type };
            nint query = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, QueryDesc*, nint*, int>)V(_device, 24))(_device, &description, &query));
            return query;
        }

        private static byte[] Compile(string source, string entry, string profile)
            => Bytecode.GetOrAdd((source, entry, profile), static key =>
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(key.Source);
                nint shader = 0, errors = 0;
                try
                {
                    int result;
                    fixed (byte* text = utf8) result = D3DCompile(text, (nuint)utf8.Length, null, 0, 0, key.Entry, key.Profile, 1 << 15, 0, &shader, &errors);
                    if (result < 0)
                    {
                        string message = errors == 0 ? "" : Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<nint, nint>)V(errors, 3))(errors)) ?? "";
                        throw new InvalidOperationException($"Refresh shader {key.Entry} failed: {message}", Marshal.GetExceptionForHR(result));
                    }
                    int length = checked((int)((delegate* unmanaged[Stdcall]<nint, nuint>)V(shader, 4))(shader));
                    byte[] bytes = new byte[length];
                    Marshal.Copy(((delegate* unmanaged[Stdcall]<nint, nint>)V(shader, 3))(shader), bytes, 0, length);
                    return bytes;
                }
                finally { Release(ref errors); Release(ref shader); }
            });

        internal static nint Query(nint pointer, Guid iid)
        {
            nint result = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)V(pointer, 0))(pointer, &iid, &result));
            return result;
        }
        internal static nint V(nint pointer, int index) => (*(nint**)pointer)[index];
        internal static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        internal static void Release(ref nint pointer)
        { nint value = pointer; pointer = 0; if (value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)V(value, 2))(value); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // A facade is one exclusive lease. Its context and all scene-specific bindings are
            // cleared before an idle backend can be rented by another worker or playing window.
            if (_context != 0) ((delegate* unmanaged[Stdcall]<nint, void>)V(_context, 110))(_context);
            for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
            _resources.Clear();
            Release(ref _swapChain);
            _swapChainScale = 0;
            var backend = new Backend
            {
                Device = _device, Context = _context, VertexShader = _vertexShader,
                Sampler = _sampler, Rasterizer = _rasterizer, Constants = _constants,
                AdapterName = AdapterName, VendorId = VendorId, FeatureLevel = FeatureLevel,
            };
            // Do not leave native aliases in a disposed facade. In particular, a delayed second
            // Dispose from an old frame must never touch a backend already rented by a new frame.
            _device = _context = _vertexShader = _sampler = _rasterizer = _constants = 0;
            bool returned = false;
            if (_allowReuse && _backendReady && backend.IsHealthy)
            {
                lock (PoolGate)
                {
                    if (!_poolShutdown && _idleBackend == null)
                    {
                        _idleBackend = backend;
                        returned = true;
                    }
                }
            }
            if (!returned) backend.Dispose();
        }

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
            uint* levels, uint levelCount, uint sdkVersion, nint* device, uint* featureLevel, nint* context);
        [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(void* source, nuint length, string? sourceName, nint defines,
            nint include, string entry, string profile, uint flags1, uint flags2, nint* code, nint* errors);

        [StructLayout(LayoutKind.Sequential)] private struct TextureDesc
        { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct SubresourceData { public nint Memory; public uint RowPitch, SlicePitch; }
        [StructLayout(LayoutKind.Sequential)] private struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)] private struct SamplerDesc
        { public uint Filter, AddressU, AddressV, AddressW; public float MipLodBias; public uint MaxAnisotropy, ComparisonFunc; public fixed float Border[4]; public float MinLod, MaxLod; }
        [StructLayout(LayoutKind.Sequential)] private struct RasterizerDesc
        { public uint FillMode, CullMode; public int FrontCounterClockwise, DepthBias; public float DepthBiasClamp, SlopeScaledDepthBias; public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable; }
        [StructLayout(LayoutKind.Sequential)] private struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }
        [StructLayout(LayoutKind.Sequential)] private struct SwapChainDesc
        { public uint Width, Height; public int Format; public int Stereo; public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct MappedResource { public nint Memory; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)] private struct QueryDesc { public uint Query, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct TimestampDisjoint { public ulong Frequency; public int Disjoint; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct AdapterDesc
        { public fixed char Description[128]; public uint VendorId, DeviceId, SubSystemId, Revision; public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory; public uint LuidLow; public int LuidHigh; }
    }
}
