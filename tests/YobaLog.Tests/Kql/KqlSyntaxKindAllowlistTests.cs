using Kusto.Language;
using Kusto.Language.Syntax;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Kql;

// Guard rail for operator-coverage drift.
//
// When Kusto.Language ships a new pipeline operator (new SyntaxKind whose name ends in
// "Operator"), our transformer sees it as "unknown" and throws UnsupportedKqlException at Apply
// — which is the correct behaviour, but means the new operator silently slides past our
// completion allowlist unless someone notices. This test enumerates every operator-kind in the
// installed Kusto.Language, checks it against a triaged set (Supported ∪ ExplicitlyUnsupported)
// and fails on any untriaged member. Updating the test IS the triage: either add to
// `SupportedOperators` (with matching transformer support) or to `ExplicitlyUnsupported`
// (meaning "we know it exists, we don't plan to handle it").
//
// Companion: KqlCompletionServiceTests already checks that every name in
// `KqlCompletionService.SupportedQueryPrefixes` appears in the completion dropdown and every
// unsupported prefix is filtered out — this test fills the other side, the SyntaxKind surface.
public sealed class KqlSyntaxKindAllowlistTests
{
	// KqlTransformer's pipeline switch handles exactly these. Keep in sync with the switch in
	// ApplyEventQuery (non-shape-changing) + ApplyShapeChanging (shape-changing). `limit` isn't
	// listed — Kusto folds `limit N` into TakeOperator, same SyntaxKind as `take`.
	static readonly HashSet<SyntaxKind> SupportedOperators =
	[
		SyntaxKind.FilterOperator,    // where
		SyntaxKind.TakeOperator,      // take, limit
		SyntaxKind.SortOperator,      // sort by, order by
		SyntaxKind.ProjectOperator,   // project
		SyntaxKind.ExtendOperator,    // extend
		SyntaxKind.CountOperator,     // count
		SyntaxKind.SummarizeOperator, // summarize
	];

	// Known to exist in Kusto.Language, known to not be supported. When Kusto adds a new
	// operator not in either bucket, the `EverySyntaxKind_EndingInOperator_IsTriaged` Fact
	// fails and forces conscious triage: either add to `SupportedOperators` and wire the
	// transformer, or add here. Strings used so the list compiles regardless of whether a
	// specific kind exists in the installed Kusto.Language version (the reflection-based
	// check resolves names lazily).
	static readonly HashSet<string> ExplicitlyUnsupportedNames = new(StringComparer.Ordinal)
	{
		"ConsumeOperator",
		"GraphWhereEdgesOperator",
		"GraphWhereNodesOperator",
		"MacroExpandOperator",
		"JoinOperator",
		"LookupOperator",
		"UnionOperator",
		"DistinctOperator",
		"TopOperator",
		"TopHittersOperator",
		"TopNestedOperator",
		"MvExpandOperator",
		"MvApplyOperator",
		"ParseOperator",
		"ParseWhereOperator",
		"ParseKvOperator",
		"EvaluateOperator",
		"ExecuteAndCacheOperator",
		"FacetOperator",
		"FindOperator",
		"SearchOperator",
		"SampleOperator",
		"SampleDistinctOperator",
		"ScanOperator",
		"SerializeOperator",
		"RenderOperator",
		"MakeSeriesOperator",
		"ReduceByOperator",
		"InvokeOperator",
		"AsOperator",
		"GetSchemaOperator",
		"PartitionOperator",
		"PartitionByOperator",
		"ProjectAwayOperator",
		"ProjectKeepOperator",
		"ProjectRenameOperator",
		"ProjectReorderOperator",
		"ProjectByNamesOperator",
		"RangeOperator",
		"PrintOperator",
		"AssertSchemaOperator",
		"ForkOperator",
		"BadQueryOperator",
		"GraphMarkComponentsOperator",
		"GraphMatchOperator",
		"GraphShortestPathsOperator",
		"GraphToTableOperator",
		"MakeGraphOperator",
	};

	[Fact]
	public void EverySyntaxKind_EndingInOperator_IsTriaged()
	{
		var allOperatorKinds = Enum.GetValues<SyntaxKind>()
			.Where(k => k.ToString().EndsWith("Operator", StringComparison.Ordinal))
			.ToHashSet();

		// Sanity: the supported bucket must only contain "Operator"-suffixed kinds (catches
		// copy-paste drift where someone put, e.g., FilterExpression into SupportedOperators).
		SupportedOperators.Should().BeSubsetOf(allOperatorKinds);

		// ExplicitlyUnsupportedNames is string-keyed to tolerate Kusto renaming / removing
		// specific kinds without compile breakage — the triage list stays declarative.
		var unsupportedAsKinds = allOperatorKinds
			.Where(k => ExplicitlyUnsupportedNames.Contains(k.ToString()))
			.ToHashSet();

		SupportedOperators.Intersect(unsupportedAsKinds).Should().BeEmpty(
			"an operator can't be simultaneously supported and explicitly-unsupported");

		var untriaged = allOperatorKinds
			.Except(SupportedOperators)
			.Except(unsupportedAsKinds)
			.OrderBy(k => k.ToString(), StringComparer.Ordinal)
			.ToList();

		untriaged.Should().BeEmpty(
			"every pipeline operator must be triaged. New Kusto.Language version? Add each to " +
			"SupportedOperators (and wire KqlTransformer) or ExplicitlyUnsupportedNames. " +
			$"Untriaged: {string.Join(", ", untriaged)}");
	}

	[Fact]
	public void SupportedOperators_ActuallyParseWithoutUnsupportedKqlException()
	{
		// Contract side of the guard: every operator we claim to support must roundtrip through
		// the parser + transformer without throwing UnsupportedKqlException. Split by which
		// transformer entry-point handles it — Apply for event-shape operators (filter / take /
		// sort), Execute for shape-changing (project / extend / count / summarize). If this
		// fails, someone added a kind to SupportedOperators without wiring transformer code.
		var applyExamples = new Dictionary<SyntaxKind, string>
		{
			[SyntaxKind.FilterOperator] = "events | where Level >= 3",
			[SyntaxKind.TakeOperator] = "events | take 10",
			[SyntaxKind.SortOperator] = "events | order by Timestamp desc",
		};
		var executeExamples = new Dictionary<SyntaxKind, string>
		{
			[SyntaxKind.ProjectOperator] = "events | project Level",
			[SyntaxKind.ExtendOperator] = "events | extend L = Level",
			[SyntaxKind.CountOperator] = "events | count",
			[SyntaxKind.SummarizeOperator] = "events | summarize count() by Level",
		};

		SupportedOperators.Should().BeEquivalentTo(applyExamples.Keys.Concat(executeExamples.Keys),
			"every operator in SupportedOperators needs a minimal parse/transform test case");

		// In-memory IQueryable is enough — we're probing the switch-cases + expression-tree
		// build. Backend translation (linq2db → SQLite) would happen only on .ToListAsync()
		// which the test never calls.
		var source = Array.Empty<EventRecord>().AsQueryable();

		foreach (var (kind, query) in applyExamples)
		{
			var code = ParseOrFail(query, kind);
			var act = () => KqlTransformer.Apply(source, code);
			act.Should().NotThrow<UnsupportedKqlException>(
				$"'{query}' ({kind}) must be handled by Apply");
		}
		foreach (var (kind, query) in executeExamples)
		{
			var code = ParseOrFail(query, kind);
			var act = () => KqlTransformer.Execute(source, code);
			act.Should().NotThrow<UnsupportedKqlException>(
				$"'{query}' ({kind}) must be handled by Execute");
		}

		static KustoCode ParseOrFail(string query, SyntaxKind kind)
		{
			var code = KustoCode.Parse(query);
			code.GetDiagnostics().Where(d => d.Severity == "Error").Should().BeEmpty(
				$"parse of '{query}' for {kind} should succeed");
			return code;
		}
	}

	[Fact]
	public void NoOperatorNamePatternChange_InKustoLanguage()
	{
		// Safety net: if Kusto refactors SyntaxKind naming (e.g., drops the `Operator` suffix),
		// this test's discovery becomes silently vacuous. Fail loudly when the shape changes.
		var operatorKinds = Enum.GetValues<SyntaxKind>()
			.Where(k => k.ToString().EndsWith("Operator", StringComparison.Ordinal))
			.ToList();
		operatorKinds.Should().HaveCountGreaterThan(20,
			"Kusto.Language historically exposes 30+ pipeline operators. " +
			"If this count collapses, the naming convention changed and the allowlist " +
			"enumeration is no longer accurate.");
	}
}
