using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace YobaLog.Core;

public readonly record struct WorkspaceId
{
	public string Value { get; }

	WorkspaceId(string value) => Value = value;

	public static readonly WorkspaceId System = new("$system");

	public bool IsValid => !string.IsNullOrEmpty(Value);

	public bool IsSystem => IsValid && Value[0] == '$';

	public override string ToString() => Value ?? string.Empty;

	public static WorkspaceId Parse(string value) =>
		TryParse(value, out var id)
			? id
			: throw new ArgumentException($"Invalid workspace id: '{value}'", nameof(value));

	public static bool TryParse([NotNullWhen(true)] string? value, out WorkspaceId id)
	{
		if (!string.IsNullOrEmpty(value) && (UserSlugRegex.IsMatch(value) || SystemSlugRegex.IsMatch(value)))
		{
			id = new WorkspaceId(value);
			return true;
		}
		id = default;
		return false;
	}

	static readonly Regex UserSlugRegex = new(
		"^[a-z0-9][a-z0-9-]{1,39}$",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	static readonly Regex SystemSlugRegex = new(
		@"^\$[a-z][a-z0-9-]{0,39}$",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
