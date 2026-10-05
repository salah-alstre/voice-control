using VoiceCommander.Core.Speech;

namespace VoiceCommander.Tests.Speech;

public class MicrophoneNameTests
{
    private const string Full = "Microphone (HyperX Cloud Alpha Wireless)";

    [Fact]
    public void Truncated_mme_name_resolves_to_the_unique_full_name()
    {
        var mme = Full[..31];
        Assert.Equal(31, mme.Length);
        Assert.Equal(Full, MicrophoneService.ResolveFullName(mme, new[] { Full, "Headset Microphone (Realtek(R) Audio)" }));
    }

    [Fact]
    public void Short_names_are_left_alone()
    {
        Assert.Equal("Microphone (USB)", MicrophoneService.ResolveFullName("Microphone (USB)", new[] { "Microphone (USB)" }));
    }

    [Fact]
    public void Ambiguous_prefix_keeps_the_mme_name()
    {
        var mme = Full[..31];
        var other = Full + " 2";
        Assert.Equal(mme, MicrophoneService.ResolveFullName(mme, new[] { Full, other }));
    }

    [Fact]
    public void No_endpoint_info_keeps_the_mme_name()
    {
        var mme = Full[..31];
        Assert.Equal(mme, MicrophoneService.ResolveFullName(mme, Array.Empty<string>()));
    }
}
