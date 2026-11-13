using Gapo.Core.Games;
using Gapo.Core.Games.Unreal;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class UnrealGameTests
{
    private const string Install = @"D:\Lib\steamapps\common\Some Game";
    private const string LocalAppData = @"C:\Users\Player\AppData\Local";
    private const string Documents = @"C:\Users\Player\Documents";

    private const string GameUserSettings =
        "[/Script/Engine.GameUserSettings]\r\n" +
        "bUseVSync=False\r\n" +
        "ResolutionSizeX=1920\r\n" +
        "ResolutionSizeY=1080\r\n" +
        "FrameRateLimit=0.000000\r\n" +
        "FullscreenMode=1\r\n" +
        "+CustomArray=First\r\n" +
        "+CustomArray=Second\r\n" +
        "\r\n" +
        "[ScalabilityGroups]\r\n" +
        "sg.ResolutionQuality=100.000000\r\n" +
        "sg.ViewDistanceQuality=3\r\n" +
        "sg.ShadowQuality=3\r\n" +
        "; a comment the game wrote\r\n" +
        "sg.PostProcessQuality=3\r\n" +
        "sg.TextureQuality=3\r\n" +
        "\r\n" +
        "[ShaderPipelineCache.CacheFile]\r\n" +
        "LastOpened=SomeGame\r\n";

    private static readonly InstalledSteamGame Game = new(4242, "Some Game", Install, @"D:\Lib", FullyInstalled: true, SizeOnDiskBytes: 0);

    [Fact]
    public void AnUntouchedDocumentIsWrittenBackByteForByte()
    {
        Assert.Equal(GameUserSettings, IniDocument.Parse(GameUserSettings).ToString());
        Assert.Equal("a=1\nb=2", IniDocument.Parse("a=1\nb=2").ToString());
    }

    [Fact]
    public void ChangingOneValueChangesOnlyThatLine()
    {
        var document = IniDocument.Parse(GameUserSettings);

        document.Set("ScalabilityGroups", "sg.ShadowQuality", "1");

        var expected = GameUserSettings.Replace("sg.ShadowQuality=3", "sg.ShadowQuality=1", StringComparison.Ordinal);
        Assert.Equal(expected, document.ToString());
    }

    [Fact]
    public void AMissingKeyIsAddedAtTheEndOfItsSectionAndAMissingSectionAtTheEnd()
    {
        var document = IniDocument.Parse(GameUserSettings);

        document.Set("scalabilitygroups", "sg.FoliageQuality", "2");
        document.Set("Brand New", "Key", "Value");

        var text = document.ToString();
        Assert.Contains("sg.TextureQuality=3\r\nsg.FoliageQuality=2\r\n\r\n[ShaderPipelineCache.CacheFile]", text, StringComparison.Ordinal);
        Assert.EndsWith("LastOpened=SomeGame\r\n\r\n[Brand New]\r\nKey=Value\r\n", text, StringComparison.Ordinal);
        Assert.Equal("2", IniDocument.Parse(text).Get("ScalabilityGroups", "sg.FoliageQuality"));
    }

    [Fact]
    public void ReadingIsCaseInsensitiveAndIgnoresCommentsAndArrays()
    {
        var document = IniDocument.Parse(GameUserSettings);

        Assert.Equal("3", document.Get("SCALABILITYGROUPS", "SG.SHADOWQUALITY"));
        Assert.Null(document.Get("ScalabilityGroups", "; a comment the game wrote"));
        Assert.Null(document.Get("/Script/Engine.GameUserSettings", "CustomArray"));
        Assert.Null(document.Get("Nowhere", "sg.ShadowQuality"));
    }

    [Fact]
    public void RemovingAKeyLeavesTheRestAlone()
    {
        var document = IniDocument.Parse(GameUserSettings);

        Assert.True(document.Remove("ScalabilityGroups", "sg.PostProcessQuality"));
        Assert.False(document.Remove("ScalabilityGroups", "sg.PostProcessQuality"));

        Assert.Equal(GameUserSettings.Replace("sg.PostProcessQuality=3\r\n", string.Empty, StringComparison.Ordinal), document.ToString());
    }

    [Fact]
    public void AValueCannotSmuggleInANewLine() =>
        Assert.Throws<ArgumentException>(() => IniDocument.Parse(GameUserSettings).Set("ScalabilityGroups", "sg.ShadowQuality", "1\r\n[Evil]"));

    [Fact]
    public void TheKnownSettingsAreReadAndDescribedInPlainWords()
    {
        var values = UnrealSettings.Read(IniDocument.Parse(GameUserSettings));

        Assert.Equal("3", values["sg.ShadowQuality"]);
        Assert.Equal("0.000000", values["FrameRateLimit"]);
        Assert.False(values.ContainsKey("sg.FoliageQuality"));
        var shadows = UnrealSettings.Known.Single(setting => setting.Key == "sg.ShadowQuality");
        Assert.Equal("Epic", UnrealSettings.Describe(shadows, "3"));
        Assert.Equal("Unlimited", UnrealSettings.Describe(UnrealSettings.Known.Single(setting => setting.Key == "FrameRateLimit"), "0.000000"));
        Assert.Equal("Borderless", UnrealSettings.Describe(UnrealSettings.Known.Single(setting => setting.Key == "FullscreenMode"), "1"));
        Assert.Equal("100 %", UnrealSettings.Describe(UnrealSettings.Known.Single(setting => setting.Key == "sg.ResolutionQuality"), "100.000000"));
        Assert.Equal("Cinematic", UnrealSettings.Describe(shadows, "4"));
        Assert.Equal("7", UnrealSettings.Describe(shadows, "7"));
    }

    [Fact]
    public void AnUnrealGameIsRecognisedByItsEngineFolderAndShippingExecutable()
    {
        var files = UnrealInstall();

        var unreal = UnrealGameDetector.Detect(Game, files, LocalAppData, Documents);

        Assert.NotNull(unreal);
        Assert.Equal("SomeGame", unreal.ProjectName);
        Assert.Equal(Install + @"\SomeGame\Binaries\Win64\SomeGame-Win64-Shipping.exe", unreal.ShippingExecutable);
        Assert.Equal(LocalAppData + @"\SomeGame\Saved\Config\Windows\GameUserSettings.ini", unreal.SettingsFileCandidates[0]);
        Assert.Contains(Documents + @"\My Games\SomeGame\Saved\Config\WindowsNoEditor\GameUserSettings.ini", unreal.SettingsFileCandidates);
        Assert.False(unreal.IsProtectedByAntiCheat);
    }

    [Fact]
    public void SettingsAreReadFromTheFirstFileThatExists()
    {
        var files = UnrealInstall();
        files.Add(LocalAppData + @"\SomeGame\Saved\Config\WindowsNoEditor\GameUserSettings.ini", GameUserSettings);
        var unreal = UnrealGameDetector.Detect(Game, files, LocalAppData, Documents)!;

        var read = UnrealGameDetector.ReadSettings(unreal, files);

        Assert.NotNull(read);
        Assert.EndsWith(@"WindowsNoEditor\GameUserSettings.ini", read.Path, StringComparison.Ordinal);
        Assert.Equal("100.000000", read.Values["sg.ResolutionQuality"]);
    }

    [Theory]
    [InlineData(@"\EasyAntiCheat\Settings.json", "Easy Anti-Cheat")]
    [InlineData(@"\EasyAntiCheat_EOS\x.dll", "Easy Anti-Cheat")]
    [InlineData(@"\BattlEye\BEClient_x64.dll", "BattlEye")]
    [InlineData(@"\BEService_x64.exe", "BattlEye")]
    public void AnAntiCheatInTheInstallFolderIsNamed(string marker, string expected)
    {
        var files = UnrealInstall();
        files.Add(Install + marker, "x");

        Assert.Equal(expected, UnrealGameDetector.Detect(Game, files, LocalAppData, Documents)?.AntiCheat);
    }

    [Fact]
    public void AGameWithoutTheEngineFolderOrAShippingExecutableIsNotUnreal()
    {
        var withoutEngine = new SteamLibraryTests.FakeFiles();
        withoutEngine.Add(Install + @"\SomeGame\Binaries\Win64\SomeGame-Win64-Shipping.exe", "x");
        var withoutShipping = new SteamLibraryTests.FakeFiles();
        withoutShipping.Add(Install + @"\Engine\Binaries\ThirdParty\x.dll", "x");
        withoutShipping.Add(Install + @"\SomeGame\Binaries\Win64\SomeGame.exe", "x");

        Assert.Null(UnrealGameDetector.Detect(Game, withoutEngine, LocalAppData, Documents));
        Assert.Null(UnrealGameDetector.Detect(Game, withoutShipping, LocalAppData, Documents));
    }

    private static SteamLibraryTests.FakeFiles UnrealInstall()
    {
        var files = new SteamLibraryTests.FakeFiles();
        files.Add(Install + @"\Engine\Binaries\ThirdParty\x.dll", "x");
        files.Add(Install + @"\SomeGame\Binaries\Win64\SomeGame-Win64-Shipping.exe", "x");
        files.Add(Install + @"\SomeGame.exe", "x");
        return files;
    }
}
