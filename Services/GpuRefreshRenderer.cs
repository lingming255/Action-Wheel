using System;
using System.IO;
using System.Numerics;
using System.Reflection;

namespace Action_Wheel.Services
{
    /// <summary>
    /// The accepted block/spectrum reference rendered entirely by D3D11 pixel shaders.
    /// Static capture/geometry are uploaded once; release updates only two timing maps.
    /// There is no frame readback in the playback path.
    /// </summary>
    internal sealed class GpuRefreshRenderer : IDisposable
    {
        internal const double PlaybackDurationMs = 2500;
        internal const double ReferenceTimeRatio = 3000.0 / PlaybackDurationMs;
        private static readonly string Source = LoadShader();
        private readonly GpuRefreshDevice.Texture _capture, _background, _blocks, _rects;
        private readonly GpuRefreshDevice.Texture _source, _pigment, _parameters;
        private readonly GpuRefreshDevice.Texture _emission, _horizontal, _near, _far;
        private readonly GpuRefreshDevice.Texture _sharp, _aux, _blur, _delta;
        private readonly GpuRefreshDevice.Shader _emissionShader, _boxShader, _sharpShader;
        private readonly GpuRefreshDevice.Shader _gaussianShader, _deltaShader, _finalShader;
        private readonly Vector4[] _constants = new Vector4[8];
        private readonly GpuRefreshDevice.Texture[] _one = new GpuRefreshDevice.Texture[1];
        private readonly GpuRefreshDevice.Texture[] _two = new GpuRefreshDevice.Texture[2];
        private readonly GpuRefreshDevice.Texture[] _four = new GpuRefreshDevice.Texture[4];
        private readonly GpuRefreshDevice.Texture[] _finalInputs = new GpuRefreshDevice.Texture[7];
        private bool _disposed;

        internal GpuRefreshDevice Device { get; }
        internal GpuRefreshDevice.Texture OutputTexture { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal double DurationMs => PlaybackDurationMs;

        internal GpuRefreshRenderer(GpuRefreshScene scene)
        {
            ArgumentNullException.ThrowIfNull(scene);
            Width = scene.Width; Height = scene.Height;
            Device = new GpuRefreshDevice();
            try
            {
                _capture = Device.CreateBgraTexture(Width, Height, scene.Capture);
                _background = Device.CreateBgraTexture(Width, Height, scene.Background);
                _blocks = Device.CreateFloatTexture(scene.Columns, scene.Rows, scene.Blocks);
                _rects = Device.CreateFloatTexture(scene.Columns, scene.Rows, scene.BlockRects);
                _source = Device.CreateFloatTexture(scene.WorkWidth, scene.WorkHeight, scene.SpectrumSource);
                _pigment = Device.CreateFloatTexture(scene.WorkWidth, scene.WorkHeight, scene.SpectrumPigment);
                _parameters = Device.CreateFloatTexture(scene.WorkWidth, scene.WorkHeight, scene.SpectrumParameters);
                _emission = Device.CreateFloatTexture(scene.BloomWidth, scene.BloomHeight);
                _horizontal = Device.CreateFloatTexture(scene.BloomWidth, scene.BloomHeight);
                _near = Device.CreateFloatTexture(scene.BloomWidth, scene.BloomHeight);
                _far = Device.CreateFloatTexture(scene.BloomWidth, scene.BloomHeight);
                _sharp = Device.CreateFloatTexture(scene.WorkWidth, scene.WorkHeight);
                int bw = Math.Max(1, (scene.WorkWidth + 3) / 4), bh = Math.Max(1, (scene.WorkHeight + 3) / 4);
                _aux = Device.CreateFloatTexture(bw, bh);
                _blur = Device.CreateFloatTexture(bw, bh);
                _delta = Device.CreateFloatTexture(scene.WorkWidth, scene.WorkHeight);
                OutputTexture = Device.CreateBgraTarget(Width, Height);
                _emissionShader = Device.CreateShader(Source, "EmissionPS");
                _boxShader = Device.CreateShader(Source, "BoxPS");
                _sharpShader = Device.CreateShader(Source, "SpectrumSharpPS");
                _gaussianShader = Device.CreateShader(Source, "GaussianPS");
                _deltaShader = Device.CreateShader(Source, "SpectrumDeltaPS");
                _finalShader = Device.CreateShader(Source, "FinalPS");
                _constants[0] = new Vector4(Width, Height, 0, 1);
                _constants[1] = new Vector4(scene.CellSize, scene.Columns, scene.BloomStep, scene.Rows);
                _constants[2] = new Vector4(scene.BloomWidth, scene.BloomHeight, scene.WorkWidth, scene.WorkHeight);
                _constants[3] = new Vector4(scene.RainColour, 0);
                _finalInputs[0] = _capture; _finalInputs[1] = _background;
                _finalInputs[2] = _blocks; _finalInputs[3] = _rects;
                _finalInputs[4] = _near; _finalInputs[5] = _far; _finalInputs[6] = _delta;
                Render(0);
                Device.Finish();
            }
            catch { Device.Dispose(); throw; }
        }

        internal void SetTimings(Vector4[] blocks, Vector4[] spectrumParameters)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Device.UpdateFloatTexture(_blocks, blocks);
            Device.UpdateFloatTexture(_parameters, spectrumParameters);
        }

        internal void Render(double elapsedMs)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool valid = double.IsFinite(elapsedMs) && elapsedMs >= 0 && elapsedMs < DurationMs;
            _constants[0].Z = valid ? (float)(elapsedMs * ReferenceTimeRatio) : 3000;
            _constants[0].W = valid ? 1 : 0;

            _two[0] = _blocks; _two[1] = _rects;
            Draw(_emissionShader, _emission, _two);
            Box(_emission, _horizontal, true, 1);
            Box(_horizontal, _near, false, 1);
            Box(_near, _horizontal, true, 2);
            Box(_horizontal, _far, false, 2);
            _two[0] = _pigment; _two[1] = _parameters;
            Draw(_sharpShader, _sharp, _two);
            Gaussian(_sharp, _aux, true, (float)_sharp.Width / _blur.Width);
            Gaussian(_aux, _blur, false, 1);
            Gaussian(_blur, _aux, true, 5.25f);
            Gaussian(_aux, _blur, false, 5.25f);
            Gaussian(_blur, _aux, true, 5.25f);
            Gaussian(_aux, _blur, false, 5.25f);
            _four[0] = _source; _four[1] = _sharp; _four[2] = _blur; _four[3] = _parameters;
            Draw(_deltaShader, _delta, _four);
            Draw(_finalShader, OutputTexture, _finalInputs);
        }

        private void Box(GpuRefreshDevice.Texture source, GpuRefreshDevice.Texture target, bool horizontal, int radius)
        {
            _constants[5] = new Vector4(radius, horizontal ? 1 : 0, 0, 0);
            _one[0] = source;
            Draw(_boxShader, target, _one);
        }

        private void Gaussian(GpuRefreshDevice.Texture source, GpuRefreshDevice.Texture target, bool horizontal, float spacing)
        {
            _constants[3].W = spacing;
            _constants[5] = new Vector4(0, horizontal ? 1 : 0, 0, 0);
            _one[0] = source;
            Draw(_gaussianShader, target, _one);
        }

        private void Draw(GpuRefreshDevice.Shader shader, GpuRefreshDevice.Texture target, GpuRefreshDevice.Texture[] inputs)
        {
            _constants[4] = new Vector4(target.Width, target.Height, inputs[0].Width, inputs[0].Height);
            Device.Draw(shader, target, inputs, _constants);
        }

        private static string LoadShader()
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ActionWheel.RefreshGpu.hlsl")
                ?? throw new InvalidDataException("The refresh GPU shaders are missing from the application.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // The device owns all registered textures and shaders, including partially built
            // construction paths. Releasing it also clears outstanding pipeline references.
            Device.Dispose();
        }
    }
}
