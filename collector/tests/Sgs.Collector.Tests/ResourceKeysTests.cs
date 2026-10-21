using Sgs.Collector.Common;

namespace Sgs.Collector.Tests;

public sealed class ResourceKeysTests
{
    [Fact]
    public void FromUri_DropsSchemeQueryAndFragment()
    {
        var uri = new Uri("https://web.sanguosha.com/220/h5_2/res/assets/baseUI/tipCommon.webp?v=-2042252689#top");

        Assert.Equal("web.sanguosha.com/220/h5_2/res/assets/baseUI/tipCommon.webp", ResourceKeys.FromUri(uri));
    }

    [Fact]
    public void FromUri_DecodesPercentEncodedChinesePath()
    {
        var encoded = new Uri("https://web.sanguosha.com/res/%E6%8A%80%E8%83%BD/a%20b.png");
        var raw = new Uri("https://web.sanguosha.com/res/技能/a b.png");

        Assert.Equal("web.sanguosha.com/res/技能/a b.png", ResourceKeys.FromUri(encoded));
        Assert.Equal("web.sanguosha.com/res/技能/a b.png", ResourceKeys.FromUri(raw));
    }

    [Theory]
    [InlineData("http://example.com:8080/a/b.json", "example.com:8080/a/b.json")]
    [InlineData("https://example.com:8443/a", "example.com:8443/a")]
    [InlineData("https://example.com:443/a", "example.com/a")]
    [InlineData("http://example.com:80/a", "example.com/a")]
    [InlineData("https://example.com:80/a", "example.com:80/a")]
    public void FromUri_KeepsOnlyNonDefaultPort(string url, string expected)
    {
        Assert.Equal(expected, ResourceKeys.FromUri(new Uri(url)));
    }

    [Fact]
    public void FromUri_LowercasesHostButKeepsPathCase()
    {
        var uri = new Uri("https://WEB.SanGuoSha.com/Res/Assets/Tip.PNG");

        Assert.Equal("web.sanguosha.com/Res/Assets/Tip.PNG", ResourceKeys.FromUri(uri));
    }

    [Fact]
    public void StripFragment_RemovesFragmentAndKeepsQuery()
    {
        var stripped = ResourceKeys.StripFragment(new Uri("https://example.com/a/b.js?v=1#section"));

        Assert.Equal("https://example.com/a/b.js?v=1", stripped.AbsoluteUri);
        Assert.Equal("", stripped.Fragment);
    }

    [Fact]
    public void StripFragment_ReturnsSameInstanceWhenThereIsNoFragment()
    {
        var uri = new Uri("https://example.com/a/b.js?v=1");

        Assert.Same(uri, ResourceKeys.StripFragment(uri));
    }

    [Fact]
    public void ForMember_JoinsPackageKeyAndMemberPathWithSlash()
    {
        var packageKey = "web.sanguosha.com/220/h5_2/res/config/Config_w.sgs";

        Assert.Equal(
            "web.sanguosha.com/220/h5_2/res/config/Config_w.sgs/cha_spell.json",
            ResourceKeys.ForMember(packageKey, "cha_spell.json"));
        Assert.Equal(
            "web.sanguosha.com/220/h5_2/res/config/Config_w.sgs/sub/dir/a.json",
            ResourceKeys.ForMember(packageKey, "sub/dir/a.json"));
    }

    [Theory]
    [InlineData("web.sanguosha.com/res/config/Config_w.sgs/cha_spell.json", "cha_spell.json")]
    [InlineData("web.sanguosha.com/res/a.png", "a.png")]
    [InlineData("a.png", "a.png")]
    [InlineData("web.sanguosha.com/res/", "")]
    public void LastSegment_ReturnsTextAfterLastSlash(string key, string expected)
    {
        Assert.Equal(expected, ResourceKeys.LastSegment(key));
    }

    [Fact]
    public void ToUrlPath_EncodesChineseAndSpacesPerSegment()
    {
        var path = ResourceKeys.ToUrlPath("web.sanguosha.com/res/技能/a b.png");

        Assert.Equal("web.sanguosha.com/res/%E6%8A%80%E8%83%BD/a%20b.png", path);
    }

    [Fact]
    public void ToUrlPath_KeepsUnreservedCharactersAndRoundTripsThroughFromUri()
    {
        var key = "web.sanguosha.com/220/h5_2/res/技能 图/a-b_c.d~e.png";

        var path = ResourceKeys.ToUrlPath(key);

        Assert.StartsWith("web.sanguosha.com/220/h5_2/res/", path, StringComparison.Ordinal);
        Assert.EndsWith("/a-b_c.d~e.png", path, StringComparison.Ordinal);
        Assert.Equal(key, ResourceKeys.FromUri(new Uri("https://" + path)));
    }
}
