using YobaLog.Web;

namespace YobaLog.Tests.Web;

public sealed class PropertyKeyContextTests
{
	[Theory]
	[InlineData("events | where Properties.", 26, "")]
	[InlineData("events | where Properties.u", 27, "u")]
	[InlineData("events | where Properties.user", 30, "user")]
	public void Match_ReturnsPrefix(string q, int pos, string expectedPrefix)
	{
		PropertyKeyContext.TryMatch(q, pos, out var editStart, out var prefix).Should().BeTrue();
		prefix.Should().Be(expectedPrefix);
		editStart.Should().Be(pos - expectedPrefix.Length);
	}

	[Theory]
	[InlineData("events | where Properties.user ==", 33)]   // cursor past the ident
	[InlineData("events | where Level == 3", 25)]           // no Properties path
	[InlineData("MyProperties.user", 17)]                   // Properties must not be suffix of another ident
	[InlineData("events | where Properties .user", 31)]     // space breaks the path
	[InlineData("", 0)]                                     // empty
	public void Match_Rejects(string q, int pos)
	{
		PropertyKeyContext.TryMatch(q, pos, out _, out _).Should().BeFalse();
	}
}
