using System.Runtime.CompilerServices;
using Kusto.Language;
using Kusto.Language.Syntax;
using LinqToDB;
using YobaLog.Core.Storage.Sqlite;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace YobaLog.Core.Kql;

#pragma warning disable CA1859 // Prefer Expression base type for AST composability.
#pragma warning disable CA1822 // Apply is instance-scoped so future options/DI can hang off the class.

sealed class KqlTransformer
{
	public const string EventsTable = "events";

	public static readonly IReadOnlyList<KqlColumn> EventRecordColumns =
	[
		new("Id", typeof(long)),
		new("Timestamp", typeof(DateTimeOffset)),
		new("Level", typeof(int)),
		new("MessageTemplate", typeof(string)),
		new("Message", typeof(string)),
		new("Exception", typeof(string)),
		new("TraceId", typeof(string)),
		new("SpanId", typeof(string)),
		new("EventId", typeof(int)),
		new("PropertiesJson", typeof(string)),
	];

	public KqlResult Execute(IQueryable<EventRecord> source, KustoCode code)
	{
		var parseErrors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
		if (parseErrors.Count > 0)
			throw new UnsupportedKqlException("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message)));

		var operators = FlattenPipeline(code.Syntax).ToList();
		if (operators.Count == 0)
			throw new UnsupportedKqlException("empty query");

		if (operators[0] is not NameReference tableName)
			throw new UnsupportedKqlException($"expected table reference, got {operators[0].GetType().Name}");
		if (!string.Equals(tableName.SimpleName, EventsTable, StringComparison.Ordinal))
			throw new UnsupportedKqlException($"unknown table '{tableName.SimpleName}'; only '{EventsTable}' is supported");

		var pipeline = operators.Skip(1).ToList();
		var splitAt = pipeline.FindIndex(IsShapeChangingOp);

		var (preOps, postOps) = splitAt < 0
			? (pipeline, new List<SyntaxNode>())
			: (pipeline.Take(splitAt).ToList(), pipeline.Skip(splitAt).ToList());

		var preResult = ApplyPipeline(source, preOps);
		var eventShape = new KqlResult(EventRecordColumns, StreamEventRecordRows(preResult));

		return postOps.Count == 0
			? eventShape
			: ApplyShapeChanges(eventShape, postOps);
	}

	static bool IsShapeChangingOp(SyntaxNode op) =>
		op is ProjectOperator or CountOperator or SummarizeOperator or ExtendOperator;

	public static bool HasShapeChangingOps(KustoCode code)
	{
		ArgumentNullException.ThrowIfNull(code);
		return FlattenPipeline(code.Syntax).Any(IsShapeChangingOp);
	}

	static KqlResult ApplyShapeChanges(KqlResult input, IReadOnlyList<SyntaxNode> ops)
	{
		var current = input;
		foreach (var op in ops)
		{
			current = op switch
			{
				ProjectOperator p => ApplyProject(current, p),
				CountOperator => ApplyCount(current),
				SummarizeOperator s => ApplySummarize(current, s),
				ExtendOperator e => ApplyExtend(current, e),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported (yet) in shape-changing pipeline"),
			};
		}
		return current;
	}

	static KqlResult ApplyProject(KqlResult input, ProjectOperator project)
	{
		var specs = new List<(string OutputName, int SourceIndex)>();
		var newColumns = new List<KqlColumn>();

		foreach (var element in project.Expressions)
		{
			var (outputName, sourceName) = element.Element switch
			{
				NameReference n => (n.SimpleName, n.SimpleName),
				SimpleNamedExpression { Name: NameDeclaration alias, Expression: NameReference src }
					=> (alias.Name.SimpleName, src.SimpleName),
				_ => throw new UnsupportedKqlException(
					$"project expression '{element.Element.Kind}' not supported (only column refs and 'alias = column')"),
			};

			var sourceIndex = FindColumnIndex(input.Columns, sourceName);
			if (sourceIndex < 0)
				throw new UnsupportedKqlException($"project: unknown column '{sourceName}'");

			specs.Add((outputName, sourceIndex));
			newColumns.Add(new KqlColumn(outputName, input.Columns[sourceIndex].ClrType));
		}

		return new KqlResult(newColumns, StreamProjected(input.Rows, [.. specs.Select(s => s.SourceIndex)]));
	}

	static KqlResult ApplyExtend(KqlResult input, ExtendOperator op)
	{
		var specs = new List<(string OutputName, int SourceIndex)>();
		var columns = new List<KqlColumn>(input.Columns);

		foreach (var element in op.Expressions)
		{
			if (element.Element is not SimpleNamedExpression { Name: NameDeclaration alias, Expression: NameReference src })
				throw new UnsupportedKqlException(
					$"extend expression '{element.Element.Kind}' not supported (only 'alias = column' for now)");

			var sourceIndex = FindColumnIndex(input.Columns, src.SimpleName);
			if (sourceIndex < 0)
				throw new UnsupportedKqlException($"extend: unknown column '{src.SimpleName}'");

			specs.Add((alias.Name.SimpleName, sourceIndex));
			columns.Add(new KqlColumn(alias.Name.SimpleName, input.Columns[sourceIndex].ClrType));
		}

		return new KqlResult(columns, StreamExtended(input.Rows, [.. specs.Select(s => s.SourceIndex)]));
	}

	static async IAsyncEnumerable<object?[]> StreamExtended(
		IAsyncEnumerable<object?[]> source,
		int[] appendIndices,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var result = new object?[row.Length + appendIndices.Length];
			Array.Copy(row, result, row.Length);
			for (var i = 0; i < appendIndices.Length; i++)
				result[row.Length + i] = row[appendIndices[i]];
			yield return result;
		}
	}

	static KqlResult ApplyCount(KqlResult input)
	{
		var columns = new[] { new KqlColumn("Count", typeof(long)) };
		return new KqlResult(columns, StreamCount(input.Rows));
	}

	static KqlResult ApplySummarize(KqlResult input, SummarizeOperator op)
	{
		var aggSpecs = new List<(string OutputName, KqlAggregate Kind)>();
		foreach (var element in op.Aggregates)
		{
			var (name, call) = element.Element switch
			{
				FunctionCallExpression f => ($"{f.Name.SimpleName}_", f),
				SimpleNamedExpression { Name: NameDeclaration alias, Expression: FunctionCallExpression f }
					=> (alias.Name.SimpleName, f),
				_ => throw new UnsupportedKqlException($"summarize aggregate '{element.Element.Kind}' not supported"),
			};

			var kind = call.Name.SimpleName switch
			{
				"count" when call.ArgumentList.Expressions.Count == 0 => KqlAggregate.Count,
				_ => throw new UnsupportedKqlException(
					$"aggregate '{call.Name.SimpleName}' not supported (only count() for now)"),
			};
			aggSpecs.Add((name, kind));
		}

		var extractors = new List<Func<object?[], object?>>();
		var outputColumns = new List<KqlColumn>();

		if (op.ByClause is not null)
		{
			foreach (var element in op.ByClause.Expressions)
			{
				switch (element.Element)
				{
					case NameReference n:
						{
							var idx = FindColumnIndex(input.Columns, n.SimpleName);
							if (idx < 0)
								throw new UnsupportedKqlException($"summarize by: unknown column '{n.SimpleName}'");
							outputColumns.Add(new KqlColumn(n.SimpleName, input.Columns[idx].ClrType));
							var captured = idx;
							extractors.Add(row => row[captured]);
							break;
						}
					case PathExpression p when IsPropertiesPath(p, out var propKey):
						{
							var propIdx = FindColumnIndex(input.Columns, nameof(Storage.Sqlite.EventRecord.PropertiesJson));
							if (propIdx < 0)
								throw new UnsupportedKqlException(
									"summarize by Properties.<key>: input shape has no PropertiesJson column");
							var path = "$." + propKey;
							outputColumns.Add(new KqlColumn("Properties." + propKey, typeof(string)));
							extractors.Add(row => KqlSqlExpressions.JsonExtract(row[propIdx] as string, path));
							break;
						}
					default:
						throw new UnsupportedKqlException(
							$"summarize by '{element.Element.Kind}' not supported (column ref or Properties.<key>)");
				}
			}
		}

		foreach (var (name, _) in aggSpecs)
			outputColumns.Add(new KqlColumn(name, typeof(long)));

		return new KqlResult(outputColumns, StreamSummarize(input.Rows, [.. extractors], aggSpecs.Count));
	}

	enum KqlAggregate { Count }

	static async IAsyncEnumerable<object?[]> StreamSummarize(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], object?>[] groupExtractors,
		int aggCount,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		var groups = new Dictionary<GroupKey, long>();

		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var keyValues = new object?[groupExtractors.Length];
			for (var i = 0; i < groupExtractors.Length; i++)
				keyValues[i] = groupExtractors[i](row);
			var key = new GroupKey(keyValues);
			groups.TryGetValue(key, out var count);
			groups[key] = count + 1;
		}

		foreach (var (key, count) in groups)
		{
			var result = new object?[key.Values.Length + aggCount];
			for (var i = 0; i < key.Values.Length; i++)
				result[i] = key.Values[i];
			for (var i = 0; i < aggCount; i++)
				result[key.Values.Length + i] = count;
			yield return result;
		}
	}

	sealed record GroupKey(object?[] Values)
	{
		public bool Equals(GroupKey? other)
		{
			if (other is null || Values.Length != other.Values.Length)
				return false;
			for (var i = 0; i < Values.Length; i++)
				if (!Equals(Values[i], other.Values[i]))
					return false;
			return true;
		}

		public override int GetHashCode()
		{
			var h = new HashCode();
			foreach (var v in Values)
				h.Add(v);
			return h.ToHashCode();
		}
	}

	static async IAsyncEnumerable<object?[]> StreamCount(
		IAsyncEnumerable<object?[]> source,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		long n = 0;
		await foreach (var _ in source.WithCancellation(ct).ConfigureAwait(false))
			n++;
		yield return [n];
	}

	static async IAsyncEnumerable<object?[]> StreamProjected(
		IAsyncEnumerable<object?[]> source,
		int[] indices,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var result = new object?[indices.Length];
			for (var i = 0; i < indices.Length; i++)
				result[i] = row[indices[i]];
			yield return result;
		}
	}

	static int FindColumnIndex(IReadOnlyList<KqlColumn> columns, string name)
	{
		for (var i = 0; i < columns.Count; i++)
			if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
				return i;
		return -1;
	}

	static IQueryable<EventRecord> ApplyPipeline(IQueryable<EventRecord> source, IReadOnlyList<SyntaxNode> operators)
	{
		var q = source;
		foreach (var op in operators)
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s),
				ProjectOperator or CountOperator or SummarizeOperator or ExtendOperator
					=> throw new UnsupportedKqlException(
						$"operator '{op.Kind}' changes result shape — the viewer currently renders event-shape only. Use 'where' / 'take' / 'order by' here; shape-changing ops work in standalone tools that consume KqlResult."),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported in yobalog"),
			};
		}
		return q;
	}

	static async IAsyncEnumerable<object?[]> StreamEventRecordRows(
		IQueryable<EventRecord> query,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var r in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
		{
			yield return
			[
				r.Id,
				DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampMs),
				r.Level,
				r.MessageTemplate,
				r.Message,
				r.Exception,
				r.TraceId,
				r.SpanId,
				r.EventId,
				r.PropertiesJson,
			];
		}
	}

	public IQueryable<EventRecord> Apply(IQueryable<EventRecord> source, KustoCode code)
	{
		var parseErrors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
		if (parseErrors.Count > 0)
			throw new UnsupportedKqlException("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message)));

		var operators = FlattenPipeline(code.Syntax).ToList();
		if (operators.Count == 0)
			throw new UnsupportedKqlException("empty query");

		if (operators[0] is not NameReference tableName)
			throw new UnsupportedKqlException($"expected table reference, got {operators[0].GetType().Name}");
		if (!string.Equals(tableName.SimpleName, EventsTable, StringComparison.Ordinal))
			throw new UnsupportedKqlException($"unknown table '{tableName.SimpleName}'; only '{EventsTable}' is supported");

		var q = source;
		foreach (var op in operators.Skip(1))
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported in yobalog"),
			};
		}
		return q;
	}

	static IQueryable<EventRecord> ApplySort(IQueryable<EventRecord> source, SortOperator sort)
	{
		IOrderedQueryable<EventRecord>? ordered = null;
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

	static IOrderedQueryable<EventRecord> ApplyOrder(
		IQueryable<EventRecord> source,
		IOrderedQueryable<EventRecord>? prior,
		string columnName,
		bool descending)
	{
		var row = Expr.Parameter(typeof(EventRecord), "e");
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
			.MakeGenericMethod(typeof(EventRecord), keyType);

		var callExpr = Expr.Call(queryableMethod, (prior ?? source).Expression, Expr.Quote(keyLambda));
		return (IOrderedQueryable<EventRecord>)source.Provider.CreateQuery<EventRecord>(callExpr);
	}

	static IEnumerable<SyntaxNode> FlattenPipeline(SyntaxNode root)
	{
		var pipes = root.GetDescendants<PipeExpression>();
		if (pipes.Count == 0)
		{
			var names = root.GetDescendants<NameReference>();
			if (names.Count > 0)
				yield return names[0];
			yield break;
		}

		PipeExpression? outermost = null;
		for (var i = 0; i < pipes.Count; i++)
		{
			if (pipes[i].Parent is not PipeExpression)
			{
				outermost = pipes[i];
				break;
			}
		}
		if (outermost is null)
			yield break;

		var stack = new Stack<SyntaxNode>();
		SyntaxNode? current = outermost;
		while (current is PipeExpression pe)
		{
			stack.Push(pe.Operator);
			current = pe.Expression;
		}
		if (current is not null)
			yield return current;
		while (stack.Count > 0)
			yield return stack.Pop();
	}

	static IQueryable<EventRecord> ApplyWhere(IQueryable<EventRecord> source, FilterOperator filter)
	{
		var row = Expr.Parameter(typeof(EventRecord), "e");
		var body = BuildExpression(filter.Condition, row);
		if (body.Type != typeof(bool))
			throw new UnsupportedKqlException($"where condition must be boolean, got {body.Type.Name}");
		var predicate = Expr.Lambda<Func<EventRecord, bool>>(body, row);
		return source.Where(predicate);
	}

	static IQueryable<EventRecord> ApplyTake(IQueryable<EventRecord> source, TakeOperator take)
	{
		if (take.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("take requires an integer literal");
		return source.Take(checked((int)n));
	}

	static Expr BuildExpression(Expression node, ParamExpr row) => node switch
	{
		BinaryExpression binary => BuildBinary(binary, row),
		FunctionCallExpression call when IsNotCall(call) => Expr.Not(BuildExpression(NotArgument(call), row)),
		ParenthesizedExpression paren => BuildExpression(paren.Expression, row),
		_ => throw new UnsupportedKqlException($"expression '{node.Kind}' not supported"),
	};

	static bool IsNotCall(FunctionCallExpression call) =>
		string.Equals(call.Name.SimpleName, "not", StringComparison.Ordinal);

	static Expression NotArgument(FunctionCallExpression call)
	{
		var args = call.ArgumentList.Expressions;
		if (args.Count != 1)
			throw new UnsupportedKqlException($"not() takes exactly one argument, got {args.Count}");
		return args[0].Element;
	}

	static Expr BuildBinary(BinaryExpression binary, ParamExpr row)
	{
		if (binary.Kind == SyntaxKind.AndExpression)
			return Expr.AndAlso(BuildExpression(binary.Left, row), BuildExpression(binary.Right, row));
		if (binary.Kind == SyntaxKind.OrExpression)
			return Expr.OrElse(BuildExpression(binary.Left, row), BuildExpression(binary.Right, row));

		var (column, access) = BuildLhs(binary.Left, row, binary.Kind);

		if (binary.Right is not LiteralExpression literal)
			throw new UnsupportedKqlException($"right side of '{binary.Kind}' must be a literal, got {binary.Right.Kind}");

		if (binary.Kind is SyntaxKind.ContainsExpression or SyntaxKind.ContainsCsExpression)
			return BuildContains(access, column, literal, binary.Kind == SyntaxKind.ContainsCsExpression);

		if (binary.Kind == SyntaxKind.HasExpression)
		{
			if (column != "Message")
				throw new UnsupportedKqlException($"'has' is only supported on Message (got '{column}')");
			if (literal.LiteralValue is not string term)
				throw new UnsupportedKqlException("'has' requires a string literal");
			return BuildFtsHas(row, term);
		}

		if (binary.Kind == SyntaxKind.HasCsExpression)
			throw new UnsupportedKqlException("'has_cs' not supported — FTS5 tokenizer is case-insensitive");

		var coerced = CoerceLiteral(literal, access.Type, column);

		return binary.Kind switch
		{
			SyntaxKind.EqualExpression => Expr.Equal(access, coerced),
			SyntaxKind.NotEqualExpression => Expr.NotEqual(access, coerced),
			SyntaxKind.LessThanExpression => Expr.LessThan(access, coerced),
			SyntaxKind.LessThanOrEqualExpression => Expr.LessThanOrEqual(access, coerced),
			SyntaxKind.GreaterThanExpression => Expr.GreaterThan(access, coerced),
			SyntaxKind.GreaterThanOrEqualExpression => Expr.GreaterThanOrEqual(access, coerced),
			_ => throw new UnsupportedKqlException($"binary '{binary.Kind}' not supported"),
		};
	}

	static (string column, Expr access) BuildLhs(Expression left, ParamExpr row, SyntaxKind op) => left switch
	{
		NameReference n => (n.SimpleName, BuildColumnAccess(row, n.SimpleName)),
		PathExpression p when IsPropertiesPath(p, out var key) =>
			("Properties." + key, BuildPropertiesAccess(row, key)),
		_ => throw new UnsupportedKqlException(
			$"left side of '{op}' must be a column name or Properties.<key>, got {left.Kind}"),
	};

	static bool IsPropertiesPath(PathExpression path, out string key)
	{
		if (path.Expression is NameReference { SimpleName: "Properties" }
			&& path.Selector is NameReference sel)
		{
			key = sel.SimpleName;
			return true;
		}
		key = "";
		return false;
	}

	static Expr BuildPropertiesAccess(ParamExpr row, string key)
	{
		var propertiesJson = Expr.Property(row, nameof(EventRecord.PropertiesJson));
		var path = Expr.Constant("$." + key, typeof(string));
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.JsonExtract))!;
		return Expr.Call(method, propertiesJson, path);
	}

	static Expr BuildColumnAccess(ParamExpr row, string column) => column switch
	{
		"Id" => Expr.Property(row, nameof(EventRecord.Id)),
		"Level" => Expr.Property(row, nameof(EventRecord.Level)),
		"LevelName" => BuildLevelName(row),
		"Timestamp" => Expr.Property(row, nameof(EventRecord.TimestampMs)),
		"TraceId" => Expr.Property(row, nameof(EventRecord.TraceId)),
		"SpanId" => Expr.Property(row, nameof(EventRecord.SpanId)),
		"Message" => Expr.Property(row, nameof(EventRecord.Message)),
		_ => throw new UnsupportedKqlException($"column '{column}' not supported (yet)"),
	};

	static Expr BuildLevelName(ParamExpr row)
	{
		var level = Expr.Property(row, nameof(EventRecord.Level));
		var toName = typeof(KqlTransformer).GetMethod(nameof(ToLevelName),
			System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
		return Expr.Call(toName, level);
	}

	internal static string ToLevelName(int level) => ((LogLevel)level).ToString();

	static Expr CoerceLiteral(LiteralExpression literal, Type targetType, string column)
	{
		if (column == "Timestamp")
		{
			if (literal.LiteralValue is not DateTime dt)
				throw new UnsupportedKqlException("Timestamp comparison requires a datetime() literal");
			var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
			return Expr.Constant(new DateTimeOffset(utc).ToUnixTimeMilliseconds());
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

	static Expr BuildFtsHas(ParamExpr row, string term)
	{
		var id = Expr.Property(row, nameof(EventRecord.Id));
		var message = Expr.Property(row, nameof(EventRecord.Message));
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.FtsHas))!;
		return Expr.Call(method, id, message, Expr.Constant(term, typeof(string)));
	}

	static Expr BuildContains(Expr access, string column, LiteralExpression literal, bool caseSensitive)
	{
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"contains requires a string column, got '{column}'");
		if (literal.LiteralValue is not string needle)
			throw new UnsupportedKqlException("contains requires a string literal");

		var method = typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!;
		var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return Expr.Call(access, method, Expr.Constant(needle), Expr.Constant(comparison));
	}
}
