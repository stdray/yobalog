using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace YobaLog.Core;

public static class PropertiesJsonSerializer
{
	public static string Serialize(
		ImmutableDictionary<string, JsonElement> properties,
		bool indented = false,
		string emptyValue = "{}")
	{
		if (properties.IsEmpty)
			return emptyValue;
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
		{
			writer.WriteStartObject();
			foreach (var (k, v) in properties)
			{
				writer.WritePropertyName(k);
				v.WriteTo(writer);
			}
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
