namespace Action_Wheel.Services;

// Timing tests need the depth container, without bringing ONNX model loading into this pure check.
internal sealed record RefreshDepthMap(int Width, int Height, float[] Values);
