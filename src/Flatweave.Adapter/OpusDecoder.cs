using System;
using Concentus;

namespace Flatweave.Adapters;

/// <summary>Float decoder contract with packet-loss concealment and FEC recovery after an invalid/lost packet.</summary>
public sealed class OpusDecoder : IDisposable
{
    readonly IOpusDecoder implementation;
    readonly int channels;
    bool previousPacketInvalid;

    public OpusDecoder(int samplingRate, int channels)
    {
        this.channels = channels;
        implementation = OpusCodecFactory.CreateDecoder(samplingRate, channels);
    }

    public int DecodePacketFloat(byte[] packetData, int count, float[] decodedSamples)
    {
        int frameSize = decodedSamples.Length / channels;
        try
        {
            int decoded = implementation.Decode(new ReadOnlySpan<byte>(packetData, 0, count), decodedSamples, frameSize, previousPacketInvalid);
            previousPacketInvalid = false;
            return decoded;
        }
        catch (OpusException)
        {
            previousPacketInvalid = true;
            return implementation.Decode(ReadOnlySpan<byte>.Empty, decodedSamples, frameSize);
        }
    }

    // Unlike DecodePacketFloat's return value, this input count includes all interleaved channels.
    public int DecodePacketLostFloat(float[] decodedSamples, int count)
    {
        previousPacketInvalid = true;
        return implementation.Decode(ReadOnlySpan<byte>.Empty, decodedSamples, count / channels);
    }

    public void Dispose() => implementation.Dispose();
}
