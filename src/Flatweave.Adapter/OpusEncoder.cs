using System;
using Concentus;
using Concentus.Enums;

namespace Flatweave.Adapters;

/// <summary>
/// Managed implementation of the shipped float encoder contract. Counts are frames per channel;
/// one-byte DTX packets are reported as zero bytes. Integer settings avoid a dependency on engine enums.
/// </summary>
public sealed class OpusEncoder : IDisposable
{
    readonly IOpusEncoder implementation;

    public OpusEncoder(int samplingRate, int channels, int bitrate, int applicationType)
    {
        implementation = OpusCodecFactory.CreateEncoder(samplingRate, channels, (OpusApplication)applicationType);
        implementation.Bitrate = bitrate;
    }

    // The shipped float path does not use this setting to change the supplied frame size.
    public int EncoderDelay { get; set; }
    public int Complexity { get => implementation.Complexity; set => implementation.Complexity = value; }
    public int SignalHint { get => (int)implementation.SignalType; set => implementation.SignalType = (OpusSignal)value; }

    public int Encode(float[] pcmSamples, int count, byte[] encodedData)
    {
        int size = implementation.Encode(pcmSamples, count, encodedData, encodedData.Length);
        return size <= 1 ? 0 : size;
    }

    public void Dispose() => implementation.Dispose();
}
