using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>Hermetic unit tests for the pure <see cref="WindowsPathMapper"/> sanitizer.</summary>
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
}
