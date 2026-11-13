using Xunit;

namespace Gapo.Sandbox.Contract.Tests;

public sealed class SandboxSettingsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("gapo-sandbox-settings-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void DefaultsAreTheUnoptimisedConfiguration()
    {
        var settings = SandboxSettings.UnoptimisedDefaults();

        Assert.True(settings.IsUnoptimisedDefaults());
        Assert.Equal(AntiAliasing.SSAA4x, settings.AntiAliasing);
        Assert.Equal(1, settings.RenderThreads);
        Assert.Equal(10, settings.AutosaveSeconds);
    }

    [Fact]
    public void RevisionAndAuthorDoNotMakeSettingsOptimised()
    {
        var settings = SandboxSettings.UnoptimisedDefaults();
        settings.Revision = 9;
        settings.ModifiedBy = "Someone";

        Assert.True(settings.IsUnoptimisedDefaults());
    }

    [Fact]
    public void AnyChangedSettingIsNoLongerTheDefaults()
    {
        var settings = SandboxSettings.UnoptimisedDefaults();
        settings.PhysicsSubsteps = 4;

        Assert.False(settings.IsUnoptimisedDefaults());
    }

    [Fact]
    public void IniRoundTripPreservesEverySetting()
    {
        var original = new SandboxSettings
        {
            ResolutionScale = 70,
            AntiAliasing = AntiAliasing.FXAA,
            RenderDistance = 900,
            TerrainLod = true,
            Shadows = ShadowQuality.High,
            WaterReflections = WaterQuality.Simple,
            Clouds = CloudQuality.Low,
            Fog = FogQuality.Simple,
            Particles = ParticleQuality.Medium,
            Textures = TextureQuality.High,
            SimulationDistance = SimulationDistance.Far,
            EntityDensity = 75,
            PhysicsSubsteps = 2,
            RenderThreads = 4,
            FrameRateCap = 60,
            VSync = true,
            AutosaveSeconds = 300,
            Revision = 12,
            ModifiedBy = "GAPO (test; with a semicolon)",
        };

        var parsed = SandboxSettings.FromIni(original.ToIni());

        Assert.Equal(original.ToIni(), parsed.ToIni());
        Assert.Equal("GAPO (test; with a semicolon)", parsed.ModifiedBy);
    }

    [Fact]
    public void ValuesOutsideTheirRangeAreClamped()
    {
        var parsed = SandboxSettings.FromIni("ResolutionScale=5\nRenderDistance=99999\nPhysicsSubsteps=0\nRenderThreads=500\n");

        Assert.Equal(50, parsed.ResolutionScale);
        Assert.Equal(1600, parsed.RenderDistance);
        Assert.Equal(1, parsed.PhysicsSubsteps);
        Assert.Equal(64, parsed.RenderThreads);
    }

    [Fact]
    public void UnreadableValuesFallBackToTheDefaults()
    {
        var parsed = SandboxSettings.FromIni("AntiAliasing=Sharpest\nShadows=7\nVSync=perhaps\nPhysicsSubsteps=many\n");

        Assert.Equal(AntiAliasing.SSAA4x, parsed.AntiAliasing);
        Assert.Equal(ShadowQuality.Ultra, parsed.Shadows);
        Assert.False(parsed.VSync);
        Assert.Equal(8, parsed.PhysicsSubsteps);
    }

    [Fact]
    public void KeysAreCaseInsensitiveAndTrailingNotesAreIgnored()
    {
        var parsed = SandboxSettings.FromIni("[Graphics]\nantialiasing = fxaa   ; a note\nTERRAINLOD=on\n");

        Assert.Equal(AntiAliasing.FXAA, parsed.AntiAliasing);
        Assert.True(parsed.TerrainLod);
    }

    [Fact]
    public void SaveThenLoadReturnsTheSameSettings()
    {
        var path = Path.Combine(directory, "nested", "sandbox-settings.ini");
        var settings = SandboxSettings.UnoptimisedDefaults();
        settings.RenderThreads = 2;
        settings.Revision = 3;

        settings.Save(path);
        var loaded = SandboxSettings.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(settings.ToIni(), loaded.ToIni());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SaveReplacesAnExistingFile()
    {
        var path = Path.Combine(directory, "sandbox-settings.ini");
        SandboxSettings.UnoptimisedDefaults().Save(path);
        var changed = SandboxSettings.UnoptimisedDefaults();
        changed.AutosaveSeconds = 300;

        changed.Save(path);

        Assert.Equal(300, SandboxSettings.Load(path)!.AutosaveSeconds);
    }

    [Fact]
    public void LoadReturnsNothingForAMissingFile()
    {
        Assert.Null(SandboxSettings.Load(Path.Combine(directory, "absent.ini")));
        Assert.Null(SandboxSettings.Load(Path.Combine(directory, "absent-directory", "absent.ini")));
    }
}
