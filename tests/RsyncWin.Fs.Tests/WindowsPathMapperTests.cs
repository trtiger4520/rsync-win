using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>Hermetic unit tests for the pure <see cref="WindowsPathMapper"/> sanitizer.</summary>
[Trait("Category", "WindowsFs")]
public class WindowsPathMapperTests
{
    [Theory]
    [InlineData("con.txt", "con_.txt")]
    [InlineData("NUL", "NUL_")]
    [InlineData("com1.log", "com1_.log")]
    [InlineData("lpt9", "lpt9_")]
    public void ReservedDeviceNames_GetAnUnderscoreAppendedToTheBase(string name, string expected)
    {
        (string mapped, bool changed) = WindowsPathMapper.Map(name);
        Assert.Equal(expected, mapped);
        Assert.True(changed);
    }

    [Fact]
    public void TrailingDot_GetsAnUnderscoreAppended()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map("name.");
        Assert.Equal("name._", mapped);
        Assert.True(changed);
    }

    [Fact]
    public void TrailingSpace_GetsAnUnderscoreAppended()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map("name ");
        Assert.Equal("name _", mapped);
        Assert.True(changed);
    }

    [Fact]
    public void ControlCharacter_IsReplacedWithUnderscore()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map("name\u0001");
        Assert.Equal("name_", mapped);
        Assert.True(changed);
    }

    [Fact]
    public void AllInvalidCharsSegment_MapsToUnderscore()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map(":");
        Assert.Equal("_", mapped);
        Assert.True(changed);
    }

    [Fact]
    public void MultiSegmentPath_SanitizesEachSegmentIndependently()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map("a:b/c.");
        Assert.Equal(@"a_b\c._", mapped);
        Assert.True(changed);
    }

    [Fact]
    public void AlreadySafeName_IsUnchanged()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map("subdir/nested.txt");
        Assert.Equal(@"subdir\nested.txt", mapped);
        Assert.False(changed);
    }

    [Fact]
    public void WindowsPolicy_PreservesWindowsMappingAndComparisonSemantics()
    {
        (string mapped, bool changed) = LocalPathPolicy.Windows.Map("CON/a:b");

        Assert.Equal(@"CON_\a_b", mapped);
        Assert.True(changed);
        Assert.Equal('\\', LocalPathPolicy.Windows.DirectorySeparator);
        Assert.True(LocalPathPolicy.Windows.PathComparer.Equals("File.txt", "file.txt"));
    }

    [Fact]
    public void UnixPolicy_PreservesLegalUnixNamesAndUsesOrdinalComparison()
    {
        const string name = "CON/a:b/name. /back\\slash";

        (string mapped, bool changed) = LocalPathPolicy.Unix.Map(name);

        Assert.Equal(name, mapped);
        Assert.False(changed);
        Assert.Equal('/', LocalPathPolicy.Unix.DirectorySeparator);
        Assert.False(LocalPathPolicy.Unix.PathComparer.Equals("File.txt", "file.txt"));
    }

    [Fact]
    public void PlatformPolicies_ApplyDifferentCollisionRulesDeterministically()
    {
        string windowsFirst = LocalPathPolicy.Windows.Map("a:b").Mapped;
        string windowsSecond = LocalPathPolicy.Windows.Map("a_b").Mapped;
        string unixFirst = LocalPathPolicy.Unix.Map("a:b").Mapped;
        string unixSecond = LocalPathPolicy.Unix.Map("a_b").Mapped;

        Assert.True(LocalPathPolicy.Windows.PathComparer.Equals(windowsFirst, windowsSecond));
        Assert.False(LocalPathPolicy.Unix.PathComparer.Equals(unixFirst, unixSecond));
    }

    [Theory]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("con.log.backup")]
    [InlineData("nUl.txt")]
    public void EveryReservedDeviceBase_IsMappedToANameThatIsNotReserved(string name)
    {
        (string mapped, bool changed) = WindowsPathMapper.Map(name);

        Assert.True(changed);
        Assert.DoesNotContain(mapped.Split('\\'), segment =>
            segment.Equals(name.Split('.')[0], StringComparison.OrdinalIgnoreCase));
        Assert.Equal(mapped, WindowsPathMapper.Map(mapped).Mapped);
    }

    [Theory]
    [InlineData("name\u0000", "name_")]
    [InlineData("name\u001f", "name_")]
    [InlineData("a*b", "a_b")]
    [InlineData("a?b", "a_b")]
    [InlineData("a\"b", "a_b")]
    [InlineData("a<b", "a_b")]
    [InlineData("a>b", "a_b")]
    [InlineData("a|b", "a_b")]
    public void InvalidWindowsCharacters_AreReplaced(string name, string expected)
    {
        Assert.Equal(expected, WindowsPathMapper.Map(name).Mapped);
    }

    [Fact]
    public void EmptySegments_AreMadeIntoOrdinarySafeNames()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map("a//b");

        Assert.Equal(@"a\_\b", mapped);
        Assert.True(changed);
        Assert.Equal(mapped, WindowsPathMapper.Map("a/_/b").Mapped);
    }

    [Fact]
    public void NavigationTokensRemainVisibleToTheContainmentBackstop()
    {
        (string mapped, bool changed) = WindowsPathMapper.Map("../outside");

        Assert.Equal(@"..\outside", mapped);
        Assert.False(changed);
    }
}
