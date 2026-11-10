using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Relative inverse depth, top-down like the input capture. Values are normalized to 0..1;
    /// larger values represent nearer content. This is an independent learned depth model,
    /// not the Jianying model and not a colour/detail heuristic.
    /// </summary>
    internal sealed record RefreshDepthMap(int Width, int Height, float[] Values);

    /// <summary>
    /// Runs Depth Anything V2 Small on the capture worker. One CPU session is retained for
    /// the application's lifetime; the gate prevents overlapping gestures from running two
    /// model inferences or competing for the same CPU pool. No downloads occur at runtime.
    /// </summary>
    internal static class RefreshDepthEstimator
    {
        private const string ModelFileName = "depth-anything-v2-small-quantized.onnx";
        private const string ModelSha256 = "FCF51F1B230362B28690BB9D1809BF0431F29CAD20534E3F589BD7285547F20D";
        private const int InferenceLongestSide = 392;
        private static readonly object Gate = new();
        private static InferenceSession? _session;

        internal static string ModelPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ActionWheel", "Models", ModelFileName);

        public static RefreshDepthMap Estimate(byte[] bgra, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(bgra);
            if (width <= 0 || height <= 0 || bgra.LongLength != (long)width * height * 4)
                throw new ArgumentException("Depth input must be a complete top-down BGRA capture.", nameof(bgra));

            lock (Gate)
            {
                InferenceSession session = _session ??= LoadSession();
                // This ONNX export accepts dynamic spatial dimensions in multiples of 14.
                // Bound the longest side for interactive desktop use, preserving aspect ratio;
                // this is deliberately smaller than the model card's 518-pixel processor size.
                double scale = (double)InferenceLongestSide / Math.Max(width, height);
                int inputWidth = Math.Max(14, (int)Math.Round(width * scale / 14) * 14);
                int inputHeight = Math.Max(14, (int)Math.Round(height * scale / 14) * 14);
                float[] inputData = ResizeAndNormalize(bgra, width, height, inputWidth, inputHeight);
                var inputTensor = new DenseTensor<float>(inputData, [1, 3, inputHeight, inputWidth]);
                using var results = session.Run(
                    [NamedOnnxValue.CreateFromTensor("pixel_values", inputTensor)]);
                Tensor<float> prediction = results.Single(result => result.Name == "predicted_depth").AsTensor<float>();
                ReadOnlySpan<int> dimensions = prediction.Dimensions;
                if (dimensions.Length != 3 || dimensions[0] != 1
                    || dimensions[1] <= 0 || dimensions[2] <= 0)
                    throw new InvalidDataException("Depth model returned an unexpected tensor shape.");

                int outputHeight = dimensions[1], outputWidth = dimensions[2];
                float[] depth = prediction.ToArray();
                if (depth.LongLength != (long)outputWidth * outputHeight)
                    throw new InvalidDataException("Depth model returned an incomplete image.");
                NormalizeDepth(depth);
                return new RefreshDepthMap(outputWidth, outputHeight, depth);
            }
        }

        private static InferenceSession LoadSession()
        {
            string path = ModelPath;
            if (!File.Exists(path))
                throw new FileNotFoundException("The local Depth Anything V2 Small model is required for refresh playback.", path);
            using (FileStream stream = File.OpenRead(path))
            {
                string actualHash = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(actualHash, ModelSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The local refresh depth model does not match its pinned SHA-256.");
            }

            using var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 4),
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            // The default provider is the CPU provider. Missing/invalid model dependencies
            // propagate to the caller; there is deliberately no invented-depth fallback.
            var session = new InferenceSession(path, options);
            if (!session.InputMetadata.ContainsKey("pixel_values")
                || !session.OutputMetadata.ContainsKey("predicted_depth"))
            {
                session.Dispose();
                throw new InvalidDataException("The pinned depth model has unexpected input/output names.");
            }
            return session;
        }

        private static float[] ResizeAndNormalize(byte[] bgra, int width, int height,
            int targetWidth, int targetHeight)
        {
            int plane = checked(targetWidth * targetHeight);
            var tensor = new float[checked(plane * 3)];
            double xScale = (double)width / targetWidth, yScale = (double)height / targetHeight;
            for (int y = 0; y < targetHeight; y++)
            {
                double sourceY = (y + 0.5) * yScale - 0.5;
                int firstY = (int)Math.Floor(sourceY);
                float vertical = (float)(sourceY - firstY);
                int top = Math.Clamp(firstY, 0, height - 1) * width;
                int bottom = Math.Clamp(firstY + 1, 0, height - 1) * width;
                for (int x = 0; x < targetWidth; x++)
                {
                    double sourceX = (x + 0.5) * xScale - 0.5;
                    int firstX = (int)Math.Floor(sourceX);
                    float horizontal = (float)(sourceX - firstX);
                    int left = Math.Clamp(firstX, 0, width - 1), right = Math.Clamp(firstX + 1, 0, width - 1);
                    int topLeft = (top + left) * 4, topRight = (top + right) * 4;
                    int bottomLeft = (bottom + left) * 4, bottomRight = (bottom + right) * 4;
                    int pixel = y * targetWidth + x;
                    // RGB NCHW, the export's verified ImageNet mean/std. GDI capture alpha
                    // is undefined and never enters inference. Bilinear resizing uses pixel
                    // centres and clamped edges so the returned map shares capture coordinates.
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int bgraChannel = 2 - channel;
                        float upper = bgra[topLeft + bgraChannel]
                            + (bgra[topRight + bgraChannel] - bgra[topLeft + bgraChannel]) * horizontal;
                        float lower = bgra[bottomLeft + bgraChannel]
                            + (bgra[bottomRight + bgraChannel] - bgra[bottomLeft + bgraChannel]) * horizontal;
                        float value = (upper + (lower - upper) * vertical) / 255f;
                        float mean = channel == 0 ? 0.485f : channel == 1 ? 0.456f : 0.406f;
                        float deviation = channel == 0 ? 0.229f : channel == 1 ? 0.224f : 0.225f;
                        tensor[channel * plane + pixel] = (value - mean) / deviation;
                    }
                }
            }
            return tensor;
        }

        private static void NormalizeDepth(float[] depth)
        {
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            foreach (float value in depth)
            {
                if (!float.IsFinite(value))
                    throw new InvalidDataException("Depth model produced a non-finite prediction.");
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
            float range = maximum - minimum;
            if (!(range > 0))
            {
                Array.Clear(depth);
                return;
            }
            for (int i = 0; i < depth.Length; i++)
                depth[i] = Math.Clamp((depth[i] - minimum) / range, 0, 1);
        }
    }
}
