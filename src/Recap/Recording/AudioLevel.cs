using NAudio.Wave;

namespace Recap.Recording;

public static class AudioLevel
{
    public static float Calculate(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0)
        {
            return 0;
        }

        try
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                var samples = bytesRecorded / 4;
                if (samples == 0) return 0;

                double sum = 0;
                for (var i = 0; i < bytesRecorded; i += 4)
                {
                    var sample = BitConverter.ToSingle(buffer, i);
                    sum += sample * sample;
                }

                return Clamp((float)Math.Sqrt(sum / samples));
            }

            if (format.BitsPerSample == 16)
            {
                var samples = bytesRecorded / 2;
                if (samples == 0) return 0;

                double sum = 0;
                for (var i = 0; i < bytesRecorded; i += 2)
                {
                    var sample = BitConverter.ToInt16(buffer, i) / 32768f;
                    sum += sample * sample;
                }

                return Clamp((float)Math.Sqrt(sum / samples));
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private static float Clamp(float value) => Math.Max(0, Math.Min(1, value));
}
