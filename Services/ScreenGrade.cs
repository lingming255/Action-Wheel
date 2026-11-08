using System;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Drains the colour out of every monitor while the wheel gesture is held, through the
    /// Magnification API's full-screen colour matrix.
    /// </summary>
    /// <remarks>
    /// The matrix is applied by DWM to the composed desktop, so it covers every window - the ring
    /// included - at no cost to this process and without a screenshot. Windows drops the effect
    /// when the process that set it exits, so a crash cannot leave the user's screen grey.
    ///
    /// UI thread only: LauncherService lowers and restores from its dispatcher queue, which is
    /// what orders a late Lower against the release that should cancel it.
    /// </remarks>
    internal static class ScreenGrade
    {
        /// <summary>Saturation left while held: 0 is pure grey, 1 is untouched.</summary>
        public const float Saturation = 0.1f;

        // Rec. 601 luma.
        public const float LumR = 0.30f, LumG = 0.59f, LumB = 0.11f;

        [DllImport("Magnification.dll")] private static extern bool MagInitialize();
        [DllImport("Magnification.dll")] private static extern bool MagUninitialize();
        [DllImport("Magnification.dll")] private static extern bool MagSetFullscreenColorEffect(float[] matrix);

        private static readonly float[] Identity =
        {
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0,
            0, 0, 0, 0, 1,
        };

        private static bool _initialized;
        private static bool _lowered;

        /// <summary>Desaturates every monitor.</summary>
        public static void Lower()
        {
            if (!_lowered && EnsureInitialized() && MagSetFullscreenColorEffect(BuildMatrix(Saturation)))
                _lowered = true;
        }

        /// <summary>Restores colour. A no-op when it was never lowered.</summary>
        public static void Restore()
        {
            if (!_lowered)
                return;
            _lowered = false;
            MagSetFullscreenColorEffect(Identity);
        }

        public static void Shutdown()
        {
            Restore();
            if (_initialized)
            {
                MagUninitialize();
                _initialized = false;
            }
        }

        private static bool EnsureInitialized()
        {
            if (!_initialized)
            {
                try { _initialized = MagInitialize(); }
                catch (DllNotFoundException) { _initialized = false; }
            }
            return _initialized;
        }

        /// <summary>
        /// Row-major 5x5 for a row vector [r g b a 1]: row i is how much input channel i feeds each
        /// output. Blends identity with the luma projection by <paramref name="saturation"/>.
        /// </summary>
        private static float[] BuildMatrix(float saturation)
        {
            float k = 1 - saturation;
            float[] luma = { LumR, LumG, LumB };
            var m = (float[])Identity.Clone();
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    m[row * 5 + col] = k * luma[row] + (row == col ? saturation : 0);
            return m;
        }
    }
}
