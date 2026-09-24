namespace MwbSwb.Core;

/// <summary>
/// SWB is optional. Stock MWB peers never see this; probe failures must not affect MWB.
/// </summary>
public static class SwbCapability
{
    public const string FeatureId = "swb-stereo-matrix";
    public const int Version = 1;

    /// <summary>True only when remote completed SWB handshake successfully.</summary>
    public static bool PeerHasSwb(SwbPeerInfo? peer) => peer is { StereoOk: true } || peer != null;
}