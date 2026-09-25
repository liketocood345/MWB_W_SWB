using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MwbSwb.Audio;

/// <summary>
/// Normalize any capture format to interleaved stereo IEEE-float at the matrix sample rate.
/// </summary>
public static class StereoPcmCodec
{
    public const int TargetChannels = 2;

    public static ISampleProvider ToStereoFloat(IWaveProvider source)
    {
        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.Channels == 1)
            return new MonoToStereoSampleProvider(samples);
        if (samples.WaveFormat.Channels == 2)
            return samples;
        return new MultiplexingSampleProvider(new[] { samples }, TargetChannels);
    }

    /// <summary>Stereo float, resampled to <paramref name="targetSampleRate"/> when needed.</summary>
    public static ISampleProvider ToStereoFloatAtRate(IWaveProvider source, int targetSampleRate)
    {
        var stereo = ToStereoFloat(source);
        if (stereo.WaveFormat.SampleRate == targetSampleRate)
            return stereo;
        return new WdlResamplingSampleProvider(stereo, targetSampleRate);
    }

    public static WaveFormat StereoFloatFormat(int sampleRate) =>
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, TargetChannels);
}
