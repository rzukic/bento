using System.Globalization;
using System.Text;

namespace Bento.Api.Formula;

/// <summary>A problem in a formula, with the character range it refers to (end exclusive).</summary>
public sealed record FormulaError(string Message, int Start, int End);

/// <summary>Thrown by the parser at the first syntax error.</summary>
public sealed class FormulaSyntaxException(FormulaError error) : Exception(error.Message)
{
    public FormulaError Error { get; } = error;
}

// ── AST ──────────────────────────────────────────────────────────────────

public abstract record FormulaNode(int Start, int End);

public sealed record NumberLiteral(decimal Value, bool IsInteger, int Start, int End) : FormulaNode(Start, End);

public sealed record StringLiteral(string Value, int Start, int End) : FormulaNode(Start, End);

public sealed record BooleanLiteral(bool Value, int Start, int End) : FormulaNode(Start, End);

public sealed record NullLiteral(int Start, int End) : FormulaNode(Start, End);

/// <summary>A column by name (<c>amount</c>) or display name (<c>[Booked on]</c>); <c>Path</c> holds dotted parts.</summary>
public sealed record ColumnReference(IReadOnlyList<string> Path, bool Bracketed, int Start, int End) : FormulaNode(Start, End);

public sealed record UnaryExpression(string Operator, FormulaNode Operand, int Start, int End) : FormulaNode(Start, End);

public sealed record BinaryExpression(string Operator, FormulaNode Left, FormulaNode Right, int OperatorStart, int Start, int End) : FormulaNode(Start, End);

public sealed record FunctionCall(string Name, IReadOnlyList<FormulaNode> Arguments, int NameEnd, int Start, int End) : FormulaNode(Start, End)
{
    /// <summary>The last argument came after <c>WHERE</c>: <c>SUM(amount WHERE amount &gt; 0)</c>.</summary>
    public bool HasWhere { get; init; }
}

// ── Lexer ────────────────────────────────────────────────────────────────

public enum TokenKind { Number, String, Identifier, BracketIdentifier, Operator, LeftParen, RightParen, Comma, Dot, End }

public sealed record FormulaToken(TokenKind Kind, string Text, int Start, int End);

/// <summary>
/// Splits a formula into tokens. Strings use double or single quotes (a
/// doubled quote escapes it), <c>[...]</c> names a column by its display
/// name, and both <c>,</c> and <c>;</c> separate arguments.
/// </summary>
public static class FormulaLexer
{
    private static readonly string[] Operators =
        ["==", "!=", "<>", "<=", ">=", "&&", "||", "=", "<", ">", "&", "+", "-", "*", "/", "%", "^", "!"];

    public static List<FormulaToken> Tokenize(string text)
    {
        var tokens = new List<FormulaToken>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                while (i < text.Length && char.IsDigit(text[i])) i++;
                if (i < text.Length && text[i] == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                }
                if (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
                    throw Error($"'{text[start..(i + 1)]}' is not a number.", start, i + 1);
                tokens.Add(new FormulaToken(TokenKind.Number, text[start..i], start, i));
                continue;
            }

            if (c is '"' or '\'')
            {
                var value = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        throw Error("This text is missing its closing quote.", start, text.Length);
                    if (text[i] == c)
                    {
                        if (i + 1 < text.Length && text[i + 1] == c)
                        {
                            value.Append(c);
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    if (text[i] == '\0')
                        throw Error("Text cannot contain a null character.", i, i + 1);
                    value.Append(text[i++]);
                }
                tokens.Add(new FormulaToken(TokenKind.String, value.ToString(), start, i));
                continue;
            }

            if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close < 0)
                    throw Error("This column name is missing its closing ].", start, text.Length);
                var name = text[(i + 1)..close].Trim();
                if (name.Length == 0)
                    throw Error("Put a column name between [ and ].", start, close + 1);
                i = close + 1;
                tokens.Add(new FormulaToken(TokenKind.BracketIdentifier, name, start, i));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(new FormulaToken(TokenKind.Identifier, text[start..i], start, i));
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new FormulaToken(TokenKind.LeftParen, "(", i, ++i));
                    continue;
                case ')':
                    tokens.Add(new FormulaToken(TokenKind.RightParen, ")", i, ++i));
                    continue;
                case ',' or ';':
                    tokens.Add(new FormulaToken(TokenKind.Comma, c.ToString(), i, ++i));
                    continue;
                case '.':
                    tokens.Add(new FormulaToken(TokenKind.Dot, ".", i, ++i));
                    continue;
            }

            var op = Operators.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
            if (op is null)
                throw Error($"'{c}' is not allowed here.", i, i + 1);
            tokens.Add(new FormulaToken(TokenKind.Operator, op, i, i + op.Length));
            i += op.Length;
        }
        tokens.Add(new FormulaToken(TokenKind.End, "", text.Length, text.Length));
        return tokens;
    }

    internal static FormulaSyntaxException Error(string message, int start, int end) => new(new FormulaError(message, start, end));
}

// ── Parser ───────────────────────────────────────────────────────────────

/// <summary>
/// Recursive-descent parser. Precedence from loosest to tightest:
/// <c>OR ||</c>, <c>AND &amp;&amp;</c>, <c>NOT !</c>, comparisons
/// (<c>= == != &lt;&gt; &lt; &lt;= &gt; &gt;=</c>), <c>&amp;</c>, <c>+ -</c>,
/// <c>* / %</c>, unary minus, <c>^</c> (right-associative, so <c>-2^2</c> is -4).
/// A leading <c>=</c> only marks the text as a formula.
/// </summary>
public sealed class FormulaParser
{
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal) { "=", "==", "!=", "<>", "<", "<=", ">", ">=" };

    private readonly List<FormulaToken> _tokens;
    private int _position;

    private FormulaParser(List<FormulaToken> tokens)
    {
        _tokens = tokens;
    }

    public static FormulaNode Parse(string text)
    {
        var tokens = FormulaLexer.Tokenize(text);
        var parser = new FormulaParser(tokens);
        if (parser.Peek.Kind == TokenKind.Operator && parser.Peek.Text == "=")
            parser._position++;
        if (parser.Peek.Kind == TokenKind.End)
            throw FormulaLexer.Error("Write a formula, for example amount * -1.", 0, text.Length);

        var node = parser.ParseOr();
        if (parser.Peek.Kind != TokenKind.End)
        {
            var token = parser.Peek;
            throw token.Kind == TokenKind.RightParen
                ? FormulaLexer.Error("This ) has no matching (.", token.Start, token.End)
                : FormulaLexer.Error($"Expected an operator before '{Describe(token)}'.", token.Start, token.End);
        }
        return node;
    }

    private FormulaToken Peek => _tokens[_position];

    private FormulaToken Next() => _tokens[_position++];

    private bool IsKeyword(string keyword) =>
        Peek.Kind == TokenKind.Identifier &&
        string.Equals(Peek.Text, keyword, StringComparison.OrdinalIgnoreCase) &&
        _tokens[_position + 1].Kind != TokenKind.LeftParen;

    /// <summary>
    /// AND, OR, IN and WHERE after an operand: always the keyword, also before
    /// a parenthesis — <c>c AND (a OR b)</c>, <c>SUM(x WHERE (a OR b) AND c)</c>.
    /// (Before an operand, <c>AND(a, b)</c> stays the function.)
    /// </summary>
    private bool IsInfixKeyword(string keyword) =>
        Peek.Kind == TokenKind.Identifier &&
        string.Equals(Peek.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private bool IsOperator(params string[] operators) =>
        Peek.Kind == TokenKind.Operator && operators.Contains(Peek.Text);

    private FormulaNode ParseOr()
    {
        var left = ParseAnd();
        while (IsOperator("||") || IsInfixKeyword("OR"))
        {
            var op = Next();
            var right = ParseAnd();
            left = new BinaryExpression("OR", left, right, op.Start, left.Start, right.End);
        }
        return left;
    }

    private FormulaNode ParseAnd()
    {
        var left = ParseNot();
        while (IsOperator("&&") || IsInfixKeyword("AND"))
        {
            var op = Next();
            var right = ParseNot();
            left = new BinaryExpression("AND", left, right, op.Start, left.Start, right.End);
        }
        return left;
    }

    private FormulaNode ParseNot()
    {
        if (IsOperator("!") || IsKeyword("NOT"))
        {
            var op = Next();
            var operand = ParseNot();
            return new UnaryExpression("NOT", operand, op.Start, operand.End);
        }
        return ParseComparison();
    }

    private FormulaNode ParseComparison()
    {
        var left = ParseConcat();
        if (IsInfixKeyword("IN") || (IsInfixKeyword("NOT") && _tokens[_position + 1] is { Kind: TokenKind.Identifier } after &&
                                     string.Equals(after.Text, "IN", StringComparison.OrdinalIgnoreCase)))
            return ParseIn(left);
        if (Peek.Kind == TokenKind.Operator && Comparisons.Contains(Peek.Text))
        {
            var op = Next();
            var right = ParseConcat();
            var normalized = op.Text switch { "==" => "=", "!=" => "<>", _ => op.Text };
            left = new BinaryExpression(normalized, left, right, op.Start, left.Start, right.End);
            if (Peek.Kind == TokenKind.Operator && Comparisons.Contains(Peek.Text))
                throw FormulaLexer.Error("Comparisons cannot be chained; combine them with AND.", Peek.Start, Peek.End);
        }
        return left;
    }

    /// <summary>
    /// <c>category IN ("Rent", "Food")</c> and <c>NOT IN</c>: the same as
    /// <c>category = "Rent" OR category = "Food"</c>, so text ignores case as with =.
    /// </summary>
    private FormulaNode ParseIn(FormulaNode left)
    {
        var not = IsInfixKeyword("NOT") ? Next() : null;
        var @in = Next();
        if (Peek.Kind != TokenKind.LeftParen)
            throw FormulaLexer.Error("IN takes a list in parentheses: category IN (\"Rent\", \"Food\").", @in.Start, @in.End);
        var open = Next();
        var values = new List<FormulaNode>();
        while (true)
        {
            if (Peek.Kind is TokenKind.Comma or TokenKind.RightParen)
                throw FormulaLexer.Error(values.Count == 0 ? "IN needs at least one value." : "A value is missing here.", Peek.Start, Peek.End);
            values.Add(ParseConcat());
            if (Peek.Kind != TokenKind.Comma)
                break;
            Next();
        }
        if (Peek.Kind != TokenKind.RightParen)
        {
            throw Peek.Kind == TokenKind.End
                ? FormulaLexer.Error("IN ( is never closed.", @in.Start, open.End)
                : FormulaLexer.Error($"Expected , or ) in IN (…), not '{Describe(Peek)}'.", Peek.Start, Peek.End);
        }
        var close = Next();
        var any = values
            .Select(value => (FormulaNode)new BinaryExpression("=", left, value, @in.Start, left.Start, value.End))
            .Aggregate((a, b) => new BinaryExpression("OR", a, b, @in.Start, left.Start, close.End));
        return not is null ? any : new UnaryExpression("NOT", any, not.Start, close.End);
    }

    private FormulaNode ParseConcat()
    {
        var left = ParseAdditive();
        while (IsOperator("&"))
        {
            var op = Next();
            var right = ParseAdditive();
            left = new BinaryExpression("&", left, right, op.Start, left.Start, right.End);
        }
        return left;
    }

    private FormulaNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (IsOperator("+", "-"))
        {
            var op = Next();
            var right = ParseMultiplicative();
            left = new BinaryExpression(op.Text, left, right, op.Start, left.Start, right.End);
        }
        return left;
    }

    private FormulaNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (IsOperator("*", "/", "%"))
        {
            var op = Next();
            var right = ParseUnary();
            left = new BinaryExpression(op.Text, left, right, op.Start, left.Start, right.End);
        }
        return left;
    }

    private FormulaNode ParseUnary()
    {
        if (IsOperator("-", "+"))
        {
            var op = Next();
            var operand = ParseUnary();
            if (op.Text == "+")
                return operand;
            // Fold "-5" into a literal so it stays a plain number.
            return operand is NumberLiteral number && number.Start == op.End
                ? number with { Value = -number.Value, Start = op.Start }
                : new UnaryExpression("-", operand, op.Start, operand.End);
        }
        return ParsePower();
    }

    private FormulaNode ParsePower()
    {
        var left = ParsePrimary();
        if (IsOperator("^"))
        {
            var op = Next();
            var right = ParseUnary();
            return new BinaryExpression("^", left, right, op.Start, left.Start, right.End);
        }
        return left;
    }

    private FormulaNode ParsePrimary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Next();
                var isInteger = !token.Text.Contains('.');
                if (!decimal.TryParse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                    (isInteger && !long.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                    throw FormulaLexer.Error($"{token.Text} is too large.", token.Start, token.End);
                return new NumberLiteral(value, isInteger, token.Start, token.End);

            case TokenKind.String:
                Next();
                return new StringLiteral(token.Text, token.Start, token.End);

            case TokenKind.LeftParen:
            {
                Next();
                if (Peek.Kind == TokenKind.RightParen)
                    throw FormulaLexer.Error("Put an expression between the parentheses.", token.Start, Peek.End);
                var inner = ParseOr();
                if (Peek.Kind != TokenKind.RightParen)
                    throw FormulaLexer.Error("This ( is never closed.", token.Start, token.End);
                Next();
                return inner;
            }

            case TokenKind.BracketIdentifier:
                Next();
                return ParsePath(token, bracketed: true);

            case TokenKind.Identifier:
            {
                Next();
                if (Peek.Kind == TokenKind.LeftParen)
                    return ParseCall(token);
                var upper = token.Text.ToUpperInvariant();
                return upper switch
                {
                    "TRUE" => new BooleanLiteral(true, token.Start, token.End),
                    "FALSE" => new BooleanLiteral(false, token.Start, token.End),
                    "NULL" => new NullLiteral(token.Start, token.End),
                    _ => ParsePath(token, bracketed: false)
                };
            }

            case TokenKind.End:
                throw FormulaLexer.Error("The formula ends too early; something is missing here.", token.Start, token.End);

            default:
                throw FormulaLexer.Error($"Expected a value, a column or a function, not '{Describe(token)}'.", token.Start, token.End);
        }
    }

    private ColumnReference ParsePath(FormulaToken first, bool bracketed)
    {
        var parts = new List<string> { first.Text };
        var end = first.End;
        while (Peek.Kind == TokenKind.Dot)
        {
            var dot = Next();
            if (Peek.Kind is not (TokenKind.Identifier or TokenKind.BracketIdentifier))
                throw FormulaLexer.Error("Expected a column name after the dot.", dot.Start, dot.End);
            var part = Next();
            parts.Add(part.Text);
            end = part.End;
        }
        return new ColumnReference(parts, bracketed, first.Start, end);
    }

    private FunctionCall ParseCall(FormulaToken name)
    {
        var open = Next();
        var arguments = new List<FormulaNode>();
        var hasWhere = false;
        if (Peek.Kind != TokenKind.RightParen)
        {
            while (true)
            {
                if (Peek.Kind is TokenKind.Comma or TokenKind.RightParen)
                    throw FormulaLexer.Error("An argument is missing here.", Peek.Start, Peek.End);
                if (!IsInfixKeyword("WHERE"))
                    arguments.Add(ParseOr());
                if (IsInfixKeyword("WHERE"))
                {
                    // SUM(amount WHERE amount > 0), COUNT(WHERE paid): the condition ends the call.
                    var where = Next();
                    if (Peek.Kind is TokenKind.RightParen or TokenKind.End or TokenKind.Comma)
                        throw FormulaLexer.Error("Write a condition after WHERE.", where.Start, where.End);
                    arguments.Add(ParseOr());
                    hasWhere = true;
                    break;
                }
                if (Peek.Kind == TokenKind.Comma)
                {
                    Next();
                    continue;
                }
                break;
            }
        }
        if (Peek.Kind != TokenKind.RightParen)
        {
            throw Peek.Kind == TokenKind.End
                ? FormulaLexer.Error($"{name.Text}( is never closed.", name.Start, open.End)
                : FormulaLexer.Error($"Expected , or ) in {name.Text}(…), not '{Describe(Peek)}'.", Peek.Start, Peek.End);
        }
        var close = Next();
        return new FunctionCall(name.Text, arguments, name.End, name.Start, close.End) { HasWhere = hasWhere };
    }

    private static string Describe(FormulaToken token) => token.Kind switch
    {
        TokenKind.String => "\"" + token.Text + "\"",
        TokenKind.BracketIdentifier => "[" + token.Text + "]",
        TokenKind.End => "end of formula",
        _ => token.Text
    };
}

/// <summary>Walks a formula's syntax tree.</summary>
public static class FormulaNodes
{
    /// <summary>The plain names a formula uses (columns or variables), without dotted or bracketed ones.</summary>
    public static IEnumerable<string> Names(FormulaNode node) => node switch
    {
        ColumnReference { Path.Count: 1, Bracketed: false } reference => [reference.Path[0]],
        UnaryExpression unary => Names(unary.Operand),
        BinaryExpression binary => Names(binary.Left).Concat(Names(binary.Right)),
        FunctionCall call => call.Arguments.SelectMany(Names),
        _ => []
    };
}
