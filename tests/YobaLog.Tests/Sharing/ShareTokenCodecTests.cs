using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using YobaLog.Core;
using YobaLog.Core.Sharing;

namespace YobaLog.Tests.Sharing;

public sealed class ShareTokenCodecTests
{
	static ShareTokenCodec MakeCodec(string? key = null) =>
		new(Options.Create(new ShareSigningOptions
		{
			Key = key ?? Convert.ToBase64String(new byte[32]),
		}));

	[Fact]
	public void Encode_Decode_Roundtrip()
	{
		var codec = MakeCodec();
		var original = new ShareToken(
			WorkspaceId.Parse("dev"),
			"LogEvents | where Level >= 3",
			DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
			[.. new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }],
			ImmutableDictionary<string, MaskMode>.Empty
				.Add("TraceId", MaskMode.Mask)
				.Add("Properties.email", MaskMode.Hide));

		var str = codec.Encode(original);
		var decoded = codec.Decode(str);

		decoded.Should().NotBeNull();
		decoded!.Workspace.Should().Be(original.Workspace);
		decoded.Kql.Should().Be(original.Kql);
		decoded.ExpiresAt.Should().Be(original.ExpiresAt);
		decoded.Salt.Should().Equal(original.Salt);
		decoded.Modes.Should().BeEquivalentTo(original.Modes);
	}

	[Fact]
	public void Decode_WithTamperedSignature_Fails()
	{
		var codec = MakeCodec();
		var token = codec.Encode(new ShareToken(
			WorkspaceId.Parse("dev"), "LogEvents", DateTimeOffset.UtcNow.AddHours(1),
			[.. new byte[16]], ImmutableDictionary<string, MaskMode>.Empty));

		var parts = token.Split('.');
		var tampered = parts[0] + "." + new string('A', parts[1].Length);

		codec.Decode(tampered).Should().BeNull();
	}

	[Fact]
	public void Decode_WithWrongKey_Fails()
	{
		var codecA = MakeCodec(Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()));
		var codecB = MakeCodec(Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()));
		var token = codecA.Encode(new ShareToken(
			WorkspaceId.Parse("dev"), "LogEvents", DateTimeOffset.UtcNow.AddHours(1),
			[.. new byte[16]], ImmutableDictionary<string, MaskMode>.Empty));

		codecB.Decode(token).Should().BeNull();
	}

	[Fact]
	public void Decode_Garbage_Fails()
	{
		var codec = MakeCodec();
		codec.Decode("not-a-token").Should().BeNull();
		codec.Decode("").Should().BeNull();
		codec.Decode("only-one-part").Should().BeNull();
		codec.Decode(".").Should().BeNull();
	}

	[Fact]
	public void ShortKey_Rejected_OnFirstUse()
	{
		var codec = new ShareTokenCodec(Options.Create(new ShareSigningOptions
		{
			Key = Convert.ToBase64String(new byte[16]),
		}));

		var act = () => codec.Encode(new ShareToken(
			WorkspaceId.Parse("dev"), "LogEvents", DateTimeOffset.UtcNow.AddHours(1),
			[.. new byte[16]], ImmutableDictionary<string, MaskMode>.Empty));

		act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
	}

	[Fact]
	public void MissingKey_Rejected_OnFirstUse()
	{
		var codec = new ShareTokenCodec(Options.Create(new ShareSigningOptions { Key = "" }));

		var act = () => codec.Encode(new ShareToken(
			WorkspaceId.Parse("dev"), "LogEvents", DateTimeOffset.UtcNow.AddHours(1),
			[.. new byte[16]], ImmutableDictionary<string, MaskMode>.Empty));

		act.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
	}
}
