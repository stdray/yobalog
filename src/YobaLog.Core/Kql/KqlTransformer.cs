using Kusto.Language;
using Kusto.Language.Syntax;
using YobaLog.Core.Storage.Sqlite;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace YobaLog.Core.Kql;

#pragma warning disable CA1859 // Prefer Expression base type for AST composability.
#pragma warning disable CA1822 // Apply is instance-scoped so future options/DI can hang off the class.

sealed class KqlTransformer
{
	public const string EventsTable = "LogEvents";

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

		if (binary.Left is not NameReference columnRef)
			throw new UnsupportedKqlException($"left side of '{binary.Kind}' must be a column name, got {binary.Left.Kind}");
		if (binary.Right is not LiteralExpression literal)
			throw new UnsupportedKqlException($"right side of '{binary.Kind}' must be a literal, got {binary.Right.Kind}");

		var column = columnRef.SimpleName;
		var access = BuildColumnAccess(row, column);

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
