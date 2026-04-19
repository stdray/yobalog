using YobaLog.Core.Sharing;

namespace YobaLog.Tests.Sharing;

public sealed class ValueMaskerTests
{
	static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

	[Fact]
	public void SameInput_SameSalt_ProducesSameOutput()
	{
		var masker = new ValueMasker(Salt);
		var a = masker.Mask("Properties.email", "alice@example.com");
		var b = masker.Mask("Properties.email", "alice@example.com");
		a.Should().Be(b);
	}

	[Fact]
	public void DifferentSalt_ProducesDifferentOutput()
	{
		var m1 = new ValueMasker(Salt);
		var m2 = new ValueMasker([.. Enumerable.Repeat((byte)42, 16)]);
		m1.Mask("TraceId", "abc").Should().NotBe(m2.Mask("TraceId", "abc"));
	}

	[Fact]
	public void Prefix_IsLastPathSegment_Lowercased()
	{
		var m = new ValueMasker(Salt);
		m.Mask("Properties.Email", "x").Should().StartWith("email:");
		m.Mask("TraceId", "x").Should().StartWith("traceid:");
	}

	[Fact]
	public void NullOrEmpty_ReturnsEmpty()
	{
		var m = new ValueMasker(Salt);
		m.Mask("TraceId", null).Should().Be("");
		m.Mask("TraceId", "").Should().Be("");
	}

	[Fact]
	public void DifferentValues_ProduceDifferentMasks()
	{
		var m = new ValueMasker(Salt);
		m.Mask("TraceId", "abc").Should().NotBe(m.Mask("TraceId", "abd"));
	}
}
