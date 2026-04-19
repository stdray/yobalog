using YobaLog.Core.Auth;

namespace YobaLog.Tests.Auth;

public sealed class AdminPasswordHasherTests
{
	[Fact]
	public void Hash_RoundTrips()
	{
		var h = AdminPasswordHasher.Hash("s3cret");
		AdminPasswordHasher.Verify("s3cret", h).Should().BeTrue();
	}

	[Fact]
	public void Hash_WrongPassword_Rejects()
	{
		var h = AdminPasswordHasher.Hash("s3cret");
		AdminPasswordHasher.Verify("nope", h).Should().BeFalse();
	}

	[Fact]
	public void Hash_SameInput_DifferentHashes()
	{
		AdminPasswordHasher.Hash("same")
			.Should().NotBe(AdminPasswordHasher.Hash("same"), "salt is random");
	}

	[Fact]
	public void Hash_Format_IsVersionedTriple()
	{
		var h = AdminPasswordHasher.Hash("x");
		h.Split(':').Should().HaveCount(4);
		h.Should().StartWith("v1:");
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-a-hash")]
	[InlineData("v1:abc:def:ghi")]
	[InlineData("v2:600000:AAAA:BBBB")]
	[InlineData("v1:600000:not-base64:also-not")]
	public void Verify_MalformedHash_ReturnsFalse(string h)
	{
		AdminPasswordHasher.Verify("any", h).Should().BeFalse();
	}

	[Fact]
	public void Verify_EmptyPassword_ReturnsFalse()
	{
		var h = AdminPasswordHasher.Hash("x");
		AdminPasswordHasher.Verify("", h).Should().BeFalse();
	}

	[Fact]
	public void Hash_EmptyPassword_Throws()
	{
		var act = () => AdminPasswordHasher.Hash("");
		act.Should().Throw<ArgumentException>();
	}
}
