using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace YobaLog.Core.Sharing;

public sealed class ShareTokenCodec
{
	readonly Lazy<byte[]> _key;

	public ShareTokenCodec(IOptions<ShareSigningOptions> options)
	{
		_key = new Lazy<byte[]>(() =>
		{
			var raw = options.Value.Key;
			if (string.IsNullOrWhiteSpace(raw))
				throw new InvalidOperationException("ShareSigning:Key is not configured. Generate a 32+ byte base64 secret.");
			var bytes = Convert.FromBase64String(raw);
			if (bytes.Length < 32)
				throw new InvalidOperationException($"ShareSigning:Key must decode to at least 32 bytes, got {bytes.Length}.");
			return bytes;
		});
	}

	public string Encode(ShareToken token)
	{
		var dto = new Dto(
			token.Workspace.Value,
			token.Kql,
			token.ExpiresAt.ToUnixTimeMilliseconds(),
			Convert.ToBase64String(token.Salt.AsSpan()),
			token.Modes.Where(kv => kv.Value != MaskMode.Keep).ToDictionary(kv => kv.Key, kv => (int)kv.Value));

		var payloadJson = JsonSerializer.SerializeToUtf8Bytes(dto, DtoContext.Default.Dto);
		var payloadB64 = Base64Url.EncodeToString(payloadJson);
		var sig = HMACSHA256.HashData(_key.Value, Encoding.UTF8.GetBytes(payloadB64));
		var sigB64 = Base64Url.EncodeToString(sig);
		return payloadB64 + "." + sigB64;
	}

	public ShareToken? Decode(string tokenStr)
	{
		var dot = tokenStr.IndexOf('.');
		if (dot <= 0 || dot == tokenStr.Length - 1)
			return null;

		var payloadB64 = tokenStr[..dot];
		var sigB64 = tokenStr[(dot + 1)..];

		byte[] payloadBytes;
		byte[] sigBytes;
		try
		{
			payloadBytes = Base64Url.DecodeFromChars(payloadB64);
			sigBytes = Base64Url.DecodeFromChars(sigB64);
		}
		catch (FormatException)
		{
			return null;
		}

		var expected = HMACSHA256.HashData(_key.Value, Encoding.UTF8.GetBytes(payloadB64));
		if (!CryptographicOperations.FixedTimeEquals(expected, sigBytes))
			return null;

		Dto? dto;
		try
		{
			dto = JsonSerializer.Deserialize(payloadBytes, DtoContext.Default.Dto);
		}
		catch (JsonException)
		{
			return null;
		}
		if (dto is null)
			return null;

		if (!WorkspaceId.TryParse(dto.Ws, out var ws))
			return null;

		var modesBuilder = ImmutableDictionary.CreateBuilder<string, MaskMode>(StringComparer.Ordinal);
		foreach (var (k, v) in dto.Modes)
			modesBuilder[k] = (MaskMode)v;

		return new ShareToken(
			ws,
			dto.Kql,
			DateTimeOffset.FromUnixTimeMilliseconds(dto.ExpMs),
			[.. Convert.FromBase64String(dto.Salt)],
			modesBuilder.ToImmutable());
	}

	internal sealed record Dto(string Ws, string Kql, long ExpMs, string Salt, Dictionary<string, int> Modes);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(ShareTokenCodec.Dto))]
internal sealed partial class DtoContext : System.Text.Json.Serialization.JsonSerializerContext;
