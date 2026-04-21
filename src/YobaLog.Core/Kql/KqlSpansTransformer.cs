using Kusto.Language;
using Kusto.Language.Syntax;
using LinqToDB;
using YobaLog.Core.Tracing.Sqlite;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace YobaLog.Core.Kql;

#pragma warning disable CA1859 // Prefer Expression base type for AST composability.

// Minimal KQL transformer scoped to the `spans` table. H.3 ships filter-sort-limit only
// (`spans | where ... | order by ... | take N`) — the shape-changing operators (project /
// extend / summarize / count) are deferred to a later phase because:
//   (1) the waterfall UI uses GetByTraceIdAsync directly, not KQL;
//   (2) the primary browsing KQL is "find spans in a trace" / "recent long spans" which
//       are covered by where + order + take;
//   (3) generalizing the full KqlTransformer (637 lines, hardcoded around EventRecord)
//       to both targets is a much larger refactor than the immediate value justifies.
//
// Columns exposed on the `spans` target (computed via json_extract-style path where
// storage column name differs from user-facing name):
//   SpanId / TraceId / ParentSpanId  - hex strings
//   Name                              - string
//   Kind / Status                     - int (enum-cast on domain side)
//   StartTime                         - datetime literal → StartUnixNs comparison
//   Duration                          - computed (EndUnixNs - StartUnixNs), int ns
static class KqlSpansTransformer
{
	public const string SpansTable = "spans";

	public static IQueryable<SpanRecord> Apply(IQueryable<SpanRecord> source, KustoCode code)
	{
		var parseErrors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
		if (parseErrors.Count > 0)
			throw new UnsupportedKqlException("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message)));

		var operators = FlattenPipeline(code.Syntax).ToList();
		if (operators.Count == 0)
			throw new UnsupportedKqlException("empty query");

		if (operators[0] is not NameReference tableName)
			throw new UnsupportedKqlException($"expected table reference, got {operators[0].GetType().Name}");
		if (!string.Equals(tableName.SimpleName, SpansTable, StringComparison.Ordinal))
			throw new UnsupportedKqlException($"unknown table '{tableName.SimpleName}'; only '{SpansTable}' is supported on the span store");

		var q = source;
		foreach (var op in operators.Skip(1))
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s),
				_ => throw new UnsupportedKqlException(
					$"operator '{op.Kind}' not supported on spans target (only where / take / order by; project/extend/summarize deferred)"),
			};
		}
		return q;
	}

	static IEnumerable<SyntaxNode> FlattenPipeline(SyntaxNode root)
	{
		// Mirror KqlTransformer.FlattenPipeline: grab the leftmost NameReference (table
		// name) then emit each operator in order.
		var pipes = root.GetDescendants<PipeExpression>();
		if (pipes.Count == 0)
		{
			var tableRef = root.GetFirstDescendant<NameReference>();
			if (tableRef is not null)
				yield return tableRef;
			yield break;
		}

		var first = pipes[0];
		var leftmost = first.Expression.GetFirstDescendantOrSelf<NameReference>();
		if (leftmost is not null)
			yield return leftmost;

		foreach (var pipe in pipes)
			yield return pipe.Operator;
	}

	static IQueryable<SpanRecord> ApplyWhere(IQueryable<SpanRecord> source, FilterOperator filter)
	{
		var row = Expr.Parameter(typeof(SpanRecord), "r");
		var body = BuildExpression(filter.Condition, row);
		var lambda = Expr.Lambda<Func<SpanRecord, bool>>(body, row);
		return source.Where(lambda);
	}

	static IQueryable<SpanRecord> ApplyTake(IQueryable<SpanRecord> source, TakeOperator take)
	{
		if (take.Expression is not LiteralExpression lit || lit.LiteralValue is not long n)
			throw new UnsupportedKqlException("take requires an integer literal");
		return source.Take(checked((int)n));
	}

	static IQueryable<SpanRecord> ApplySort(IQueryable<SpanRecord> source, SortOperator sort)
	{
		IOrderedQueryable<SpanRecord>? ordered = null;
		foreach (var element in sort.Expressions)
		{
			var (columnName, descending) = element.Element switch
			{
				OrderedExpression { Expression: NameReference n, Ordering: var o }
					=> (n.SimpleName, !string.Equals(o?.AscOrDescKeyword?.Text, "asc", StringComparison.Ordinal)),
				NameReference n => (n.SimpleName, true),
				_ => throw new UnsupportedKqlException($"order-by expression '{element.Element.Kind}' not supported"),
			};
			ordered = ApplyOrder(source, ordered, columnName, descending);
		}
		return ordered ?? source;
	}

	static IOrderedQueryable<SpanRecord> ApplyOrder(
		IQueryable<SpanRecord> source,
		IOrderedQueryable<SpanRecord>? prior,
		string columnName,
		bool descending)
	{
		var row = Expr.Parameter(typeof(SpanRecord), "r");
		var access = BuildColumnAccess(row, columnName);
		var keyType = access.Type;
		var keyLambda = Expr.Lambda(access, row);

		var methodName = (prior is null, descending) switch
		{
			(true, true) => nameof(Queryable.OrderByDescending),
			(true, false) => nameof(Queryable.OrderBy),
			(false, true) => nameof(Queryable.ThenByDescending),
			(false, false) => nameof(Queryable.ThenBy),
		};

		var queryableMethod = typeof(Queryable).GetMethods()
			.Single(m => m.Name == methodName
				&& m.GetParameters().Length == 2
				&& m.GetGenericArguments().Length == 2)
			.MakeGenericMethod(typeof(SpanRecord), keyType);

		var callExpr = Expr.Call(queryableMethod, (prior ?? source).Expression, Expr.Quote(keyLambda));
		return (IOrderedQueryable<SpanRecord>)source.Provider.CreateQuery<SpanRecord>(callExpr);
	}

	static Expr BuildExpression(Expression node, ParamExpr row) => node switch
	{
		BinaryExpression b => BuildBinary(b, row),
		ParenthesizedExpression p => BuildExpression(p.Expression, row),
		_ => throw new UnsupportedKqlException($"where: unsupported expression kind '{node.Kind}'"),
	};

	static Expr BuildBinary(BinaryExpression binary, ParamExpr row)
	{
		var kind = binary.Kind;

		if (kind is SyntaxKind.AndExpression or SyntaxKind.OrExpression)
		{
			var left = BuildExpression(binary.Left, row);
			var right = BuildExpression(binary.Right, row);
			return kind == SyntaxKind.AndExpression
				? Expr.AndAlso(left, right)
				: Expr.OrElse(left, right);
		}

		var (column, access) = BuildLhs(binary.Left, row, kind);
		if (binary.Right is not LiteralExpression literal)
			throw new UnsupportedKqlException($"where: RHS must be a literal for column '{column}'");

		if (kind == SyntaxKind.ContainsCsExpression || kind == SyntaxKind.ContainsExpression)
		{
			if (literal.LiteralValue is not string needle)
				throw new UnsupportedKqlException("contains requires a string literal");
			if (access.Type != typeof(string))
				throw new UnsupportedKqlException($"contains requires a string column, got '{column}'");
			var method = typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!;
			var cmp = kind == SyntaxKind.ContainsCsExpression
				? StringComparison.Ordinal
				: StringComparison.OrdinalIgnoreCase;
			return Expr.Call(access, method, Expr.Constant(needle), Expr.Constant(cmp));
		}

		var rhs = CoerceLiteral(literal, access.Type, column);
		return kind switch
		{
			SyntaxKind.EqualExpression => Expr.Equal(access, rhs),
			SyntaxKind.NotEqualExpression => Expr.NotEqual(access, rhs),
			SyntaxKind.LessThanExpression => Expr.LessThan(access, rhs),
			SyntaxKind.LessThanOrEqualExpression => Expr.LessThanOrEqual(access, rhs),
			SyntaxKind.GreaterThanExpression => Expr.GreaterThan(access, rhs),
			SyntaxKind.GreaterThanOrEqualExpression => Expr.GreaterThanOrEqual(access, rhs),
			_ => throw new UnsupportedKqlException($"operator '{kind}' not supported on spans"),
		};
	}

	static (string column, Expr access) BuildLhs(Expression left, ParamExpr row, SyntaxKind op) => left switch
	{
		NameReference nr => (nr.SimpleName, BuildColumnAccess(row, nr.SimpleName)),
		_ => throw new UnsupportedKqlException("LHS must be a column reference"),
	};

	static Expr BuildColumnAccess(ParamExpr row, string column) => column switch
	{
		"SpanId" => Expr.Property(row, nameof(SpanRecord.SpanId)),
		"TraceId" => Expr.Property(row, nameof(SpanRecord.TraceId)),
		"ParentSpanId" => Expr.Property(row, nameof(SpanRecord.ParentSpanId)),
		"Name" => Expr.Property(row, nameof(SpanRecord.Name)),
		"Kind" => Expr.Property(row, nameof(SpanRecord.Kind)),
		"Status" => Expr.Property(row, nameof(SpanRecord.StatusCode)),
		// StartTime / EndTime → Unix-ns columns; datetime literals get coerced to unix-ns in CoerceLiteral.
		"StartTime" => Expr.Property(row, nameof(SpanRecord.StartUnixNs)),
		"EndTime" => Expr.Property(row, nameof(SpanRecord.EndUnixNs)),
		// Duration is computed (EndUnixNs - StartUnixNs) in nanoseconds; RHS literal must be int ns.
		"Duration" => Expr.Subtract(
			Expr.Property(row, nameof(SpanRecord.EndUnixNs)),
			Expr.Property(row, nameof(SpanRecord.StartUnixNs))),
		_ => throw new UnsupportedKqlException($"column '{column}' not available on spans target"),
	};

	static Expr CoerceLiteral(LiteralExpression literal, Type targetType, string column)
	{
		if (column is "StartTime" or "EndTime")
		{
			if (literal.LiteralValue is not DateTime dt)
				throw new UnsupportedKqlException($"{column} comparison requires a datetime() literal");
			var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
			// ns = ms × 1e6, truncating sub-millisecond like our storage.
			return Expr.Constant(new DateTimeOffset(utc).ToUnixTimeMilliseconds() * 1_000_000L);
		}

		if (targetType == typeof(long) || targetType == typeof(int))
		{
			if (literal.LiteralValue is long n)
				return targetType == typeof(int) ? Expr.Constant(checked((int)n)) : Expr.Constant(n);
			throw new UnsupportedKqlException($"{column} comparison requires an integer literal");
		}

		if (targetType == typeof(string))
		{
			if (literal.LiteralValue is string s)
				return Expr.Constant(s, typeof(string));
			throw new UnsupportedKqlException($"{column} comparison requires a string literal");
		}

		throw new UnsupportedKqlException($"cannot coerce literal for column '{column}' of type {targetType.Name}");
	}
}
