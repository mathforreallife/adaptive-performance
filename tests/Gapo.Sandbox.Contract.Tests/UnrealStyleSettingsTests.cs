using Xunit;

namespace Gapo.Sandbox.Contract.Tests;

public sealed class UnrealStyleSettingsTests
{
    private const string EpicEverything =
        "[ScalabilityGroups]\r\n" +
        "sg.ResolutionQuality=100.000000\r\n" +
        "sg.ViewDistanceQuality=3\r\n" +
        "sg.AntiAliasingQuality=3\r\n" +
        "sg.ShadowQuality=3\r\n" +
        "sg.PostProcessQuality=3\r\n" +
        "sg.EffectsQuality=4\r\n" +
        "sg.ReflectionQuality=3\r\n" +
        "sg.TextureQuality=3\r\n" +
        "sg.FoliageQuality=3\r\n" +
        "[/Script/Engine.GameUserSettings]\r\n" +
        "FrameRateLimit=0.000000\r\n" +
        "bUseVSync=False\r\n";

    private const string Lowered =
        "[ScalabilityGroups]\n" +
        "sg.ResolutionQuality=85\n" +
        "sg.ViewDistanceQuality=1\n" +
        "sg.AntiAliasingQuality=2\n" +
        "sg.ShadowQuality=0\n" +
        "sg.PostProcessQuality=1\n" +
        "sg.EffectsQuality=2\n" +
        "sg.TextureQuality=1\n" +
        "[/Script/Engine.GameUserSettings]\n" +
        "FrameRateLimit=60.000000\n" +
        "bUseVSync=True\n";

    [Fact]
    public void EpicEverythingIsTheUnoptimisedFixture()
    {
        var settings = UnrealStyleSettings.FromIni(EpicEverything);

        Assert.True(settings.IsUnoptimisedDefaults());
        Assert.Equal(UnrealStyleSettings.Author, settings.ModifiedBy);
    }

    [Fact]
    public void LowerLevelsMapOntoTheFixturesOwnSettings()
    {
        var settings = UnrealStyleSettings.FromIni(Lowered);

        Assert.Equal(85, settings.ResolutionScale);
        Assert.Equal(SimulationDistance.Far, settings.SimulationDistance);
        Assert.Equal(1000, settings.RenderDistance);
        Assert.Equal(AntiAliasing.FXAA, settings.AntiAliasing);
        Assert.Equal(ShadowQuality.Off, settings.Shadows);
        Assert.Equal(FogQuality.Simple, settings.Fog);
        Assert.Equal(ParticleQuality.Medium, settings.Particles);
        Assert.Equal(TextureQuality.Low, settings.Textures);
        Assert.Equal(60, settings.FrameRateCap);
        Assert.True(settings.VSync);
        Assert.Equal(1, settings.RenderThreads);
    }

    [Fact]
    public void AMissingOrDamagedFileMeansTheGameDefaults()
    {
        Assert.True(UnrealStyleSettings.FromIni(string.Empty).IsUnoptimisedDefaults());
        Assert.True(UnrealStyleSettings.FromIni("[ScalabilityGroups]\nsg.ShadowQuality=not a number\n").IsUnoptimisedDefaults());
    }
}
