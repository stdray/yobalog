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
		var diagnostics = code.GetDiagnostics();
		var parseErrors = diagnostics.Where(d => d.Severity == "Error").ToList();
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
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported in yobalog"),
			};
		}
		return q;
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

		// outermost = the pipe whose parent isn't another pipe
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
		_ => throw new UnsupportedKqlException($"expression '{node.Kind}' not supported"),
	};

	static Expr BuildBinary(BinaryExpression binary, ParamExpr row)
	{
		if (binary.Kind != SyntaxKind.EqualExpression)
			throw new UnsupportedKqlException($"binary '{binary.Kind}' not supported (yet)");

		if (binary.Left is not NameReference column)
			throw new UnsupportedKqlException($"left side of == must be a column name, got {binary.Left.Kind}");
		if (binary.Right is not LiteralExpression literal)
			throw new UnsupportedKqlException($"right side of == must be a literal, got {binary.Right.Kind}");

		return column.SimpleName switch
		{
			"Level" => BuildLevelEquals(row, literal),
			"TraceId" => BuildStringEquals(row, nameof(EventRecord.TraceId), literal),
			"SpanId" => BuildStringEquals(row, nameof(EventRecord.SpanId), literal),
			"Message" => BuildStringEquals(row, nameof(EventRecord.Message), literal),
			_ => throw new UnsupportedKqlException($"column '{column.SimpleName}' not supported (yet)"),
		};
	}

	static Expr BuildLevelEquals(ParamExpr row, LiteralExpression literal)
	{
		if (literal.LiteralValue is not string name)
			throw new UnsupportedKqlException("Level comparison requires a string like \"Error\"");
		if (!LogLevelParser.TryParse(name, out var level))
			throw new UnsupportedKqlException($"unknown log level '{name}'");
		var column = Expr.Property(row, nameof(EventRecord.Level));
		return Expr.Equal(column, Expr.Constant((int)level));
	}

	static Expr BuildStringEquals(ParamExpr row, string propertyName, LiteralExpression literal)
	{
		if (literal.LiteralValue is not string s)
			throw new UnsupportedKqlException($"{propertyName} comparison requires a string literal");
		var column = Expr.Property(row, propertyName);
		return Expr.Equal(column, Expr.Constant(s, typeof(string)));
	}
}
