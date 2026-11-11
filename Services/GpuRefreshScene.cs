using System;
using System.Numerics;

namespace Action_Wheel.Services
{
    /// <summary>
    /// A one-time upload of the same prepared geometry, pigment and grey LUT background used
    /// by the CPU reference. Timings retain the original three-second domain; the GPU clock
    /// advances that domain at 1.2x, preserving all envelopes in a 2.5-second playback.
    /// </summary>
    internal sealed class GpuRefreshScene
    {
        public int Width { get; }
        public int Height { get; }
        public int CellSize { get; }
        public int Columns { get; }
        public int Rows { get; }
        public int BloomStep { get; }
        public int BloomWidth { get; }
        public int BloomHeight { get; }
        public int WorkWidth { get; }
        public int WorkHeight { get; }
        public byte[] Capture { get; }
        public byte[] Background { get; }
        public Vector4[] Blocks { get; }
        public Vector4[] BlockRects { get; }
        public Vector4[] SpectrumSource { get; }
        public Vector4[] SpectrumPigment { get; }
        public Vector4[] SpectrumParameters { get; }
        public Vector3 RainColour { get; }

        public GpuRefreshScene(SpectrumOverlayRefreshRenderer reference)
        {
            Width = reference.NativeWidth; Height = reference.NativeHeight;
            UniformBlockRefreshRenderer blocks = reference.BlocksRenderer;
            CellSize = blocks.CellSize; Columns = blocks.Columns;
            Rows = (Height + CellSize - 1) / CellSize;
            BloomStep = blocks.EmissionStep; BloomWidth = blocks.EmissionWidth;
            BloomHeight = blocks.EmissionHeight;
            WorkWidth = reference.WorkWidth; WorkHeight = reference.WorkHeight;
            Capture = reference.CapturePixels; Background = blocks.PreparedBackground;
            Blocks = new Vector4[blocks.Blocks.Length];
            BlockRects = new Vector4[Blocks.Length];
            for (int i = 0; i < Blocks.Length; i++)
            {
                var block = blocks.Blocks[i];
                BlockRects[i] = new Vector4(block.Left, block.Top,
                    block.Left + block.Width, block.Top + block.Height);
            }
            int count = checked(WorkWidth * WorkHeight);
            SpectrumSource = new Vector4[count]; SpectrumPigment = new Vector4[count];
            SpectrumParameters = new Vector4[count];
            RefreshSpectrumEffect spectrum = reference.PreparedSpectrum;
            RainColour = spectrum.RainColour;
            for (int i = 0; i < count; i++)
            {
                SpectrumSource[i] = new Vector4(reference.PreparedSource[i], 0);
                SpectrumPigment[i] = new Vector4(spectrum.PreparedPigment[i], spectrum.PreparedRain[i].X);
                SpectrumParameters[i] = new Vector4(spectrum.PreparedRain[i].Y, 0, 0, 0);
            }
            UpdateTimings(reference);
        }

        public void UpdateTimings(SpectrumOverlayRefreshRenderer reference)
        {
            if (reference.NativeWidth != Width || reference.NativeHeight != Height)
                throw new ArgumentException("GPU timing source must match its prepared capture.", nameof(reference));
            for (int i = 0; i < Blocks.Length; i++)
            {
                var block = reference.BlocksRenderer.Blocks[i];
                Blocks[i] = new Vector4(block.Colour, (float)block.StartMs);
            }
            for (int i = 0; i < SpectrumParameters.Length; i++)
                SpectrumParameters[i].Y = (float)reference.PreparedStarts[i];
        }
    }
}
