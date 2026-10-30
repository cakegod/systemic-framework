using System.Collections.Immutable;
using System.Globalization;

namespace Systemic.Rules;

public interface IExpressionScope
{
    double ReadNumber(string name);
    double Count(string selector);
    double Rate(string selector, int windowTicks);
    double EventCount(string eventType, int windowTicks);
}

public enum ExpressionResultKind
{
    Number,
    Boolean,
}

public sealed class ExpressionCompileException(string message, int position)
    : FormatException($"{message} at position {position}.")
{
    public int Position { get; } = position;
}

public sealed class ExpressionEvaluationException(string message) : InvalidOperationException(message);

public sealed class CompiledExpression
{
    internal const int MaximumStackDepth = 128;
    private readonly ImmutableArray<ExpressionInstruction> _instructions;

    internal CompiledExpression(
        ImmutableArray<ExpressionInstruction> instructions,
        ExpressionResultKind resultKind,
        int stackDepth,
        ImmutableArray<string> eventTypes,
        int maximumEventWindowTicks)
    {
        _instructions = instructions;
        ResultKind = resultKind;
        StackDepth = stackDepth;
        EventTypes = eventTypes;
        MaximumEventWindowTicks = maximumEventWindowTicks;
    }

    public ExpressionResultKind ResultKind { get; }

    internal int StackDepth { get; }

    internal ImmutableArray<string> EventTypes { get; }

    internal int MaximumEventWindowTicks { get; }

    /// <summary>Gets whether the expression reads one named number from its scope.</summary>
    internal bool ReferencesNumber(string name)
    {
        foreach (var instruction in _instructions)
        {
            if (instruction.Code == ExpressionOpCode.ReadNumber
                && string.Equals(instruction.Text, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public double EvaluateNumber(IExpressionScope scope)
    {
        var value = Evaluate(scope);
        if (ResultKind != ExpressionResultKind.Number)
        {
            throw new ExpressionEvaluationException("Expression produces a boolean, not a number.");
        }

        return value.Number;
    }

    public bool EvaluateBoolean(IExpressionScope scope)
    {
        var value = Evaluate(scope);
        if (ResultKind != ExpressionResultKind.Boolean)
        {
            throw new ExpressionEvaluationException("Expression produces a number, not a boolean.");
        }

        return value.Boolean;
    }

    public bool EvaluateBoolean<TScope>(in TScope scope)
        where TScope : IExpressionScope
    {
        var value = Evaluate(scope);
        if (ResultKind != ExpressionResultKind.Boolean)
        {
            throw new ExpressionEvaluationException("Expression produces a number, not a boolean.");
        }

        return value.Boolean;
    }

    private ExpressionValue Evaluate(IExpressionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Evaluate<IExpressionScope>(scope);
    }

    private ExpressionValue Evaluate<TScope>(TScope scope)
        where TScope : IExpressionScope
    {
        Span<ExpressionValue> stack = stackalloc ExpressionValue[MaximumStackDepth];
        var top = 0;
        for (var instructionIndex = 0; instructionIndex < _instructions.Length; instructionIndex++)
        {
            var instruction = _instructions[instructionIndex];
            switch (instruction.Code)
            {
                case ExpressionOpCode.Number:
                    stack[top++] = ExpressionValue.FromNumber(instruction.Number);
                    break;
                case ExpressionOpCode.Boolean:
                    stack[top++] = ExpressionValue.FromBoolean(instruction.Boolean);
                    break;
                case ExpressionOpCode.ReadNumber:
                    stack[top++] = ExpressionValue.FromNumber(scope.ReadNumber(instruction.Text!));
                    break;
                case ExpressionOpCode.Count:
                    stack[top++] = ExpressionValue.FromNumber(scope.Count(instruction.Text!));
                    break;
                case ExpressionOpCode.Rate:
                    stack[top++] = ExpressionValue.FromNumber(scope.Rate(instruction.Text!, instruction.WindowTicks));
                    break;
                case ExpressionOpCode.EventCount:
                    stack[top++] = ExpressionValue.FromNumber(scope.EventCount(instruction.Text!, instruction.WindowTicks));
                    break;
                case ExpressionOpCode.Negate:
                    var negated = PopNumber(stack, ref top);
                    stack[top++] = ExpressionValue.FromNumber(-negated);
                    break;
                case ExpressionOpCode.Not:
                    var inverted = PopBoolean(stack, ref top);
                    stack[top++] = ExpressionValue.FromBoolean(!inverted);
                    break;
                case ExpressionOpCode.Floor:
                    var floored = PopNumber(stack, ref top);
                    stack[top++] = ExpressionValue.FromNumber(Math.Floor(floored));
                    break;
                case ExpressionOpCode.Ceiling:
                    var ceiled = PopNumber(stack, ref top);
                    stack[top++] = ExpressionValue.FromNumber(Math.Ceiling(ceiled));
                    break;
                case ExpressionOpCode.Abs:
                    var absolute = PopNumber(stack, ref top);
                    stack[top++] = ExpressionValue.FromNumber(Math.Abs(absolute));
                    break;
                case ExpressionOpCode.Add:
                    BinaryNumber(stack, ref top, static (left, right) => left + right);
                    break;
                case ExpressionOpCode.Subtract:
                    BinaryNumber(stack, ref top, static (left, right) => left - right);
                    break;
                case ExpressionOpCode.Multiply:
                    BinaryNumber(stack, ref top, static (left, right) => left * right);
                    break;
                case ExpressionOpCode.Divide:
                    BinaryNumber(stack, ref top, Divide);
                    break;
                case ExpressionOpCode.Modulo:
                    BinaryNumber(stack, ref top, Modulo);
                    break;
                case ExpressionOpCode.Minimum:
                    BinaryNumber(stack, ref top, Math.Min);
                    break;
                case ExpressionOpCode.Maximum:
                    BinaryNumber(stack, ref top, Math.Max);
                    break;
                case ExpressionOpCode.Clamp:
                    var maximum = PopNumber(stack, ref top);
                    var minimum = PopNumber(stack, ref top);
                    var value = PopNumber(stack, ref top);
                    if (minimum > maximum)
                    {
                        throw new ExpressionEvaluationException("clamp minimum exceeds its maximum.");
                    }

                    stack[top++] = ExpressionValue.FromNumber(Math.Clamp(value, minimum, maximum));
                    break;
                case ExpressionOpCode.Less:
                    BinaryComparison(stack, ref top, static (left, right) => left < right);
                    break;
                case ExpressionOpCode.LessOrEqual:
                    BinaryComparison(stack, ref top, static (left, right) => left <= right);
                    break;
                case ExpressionOpCode.Greater:
                    BinaryComparison(stack, ref top, static (left, right) => left > right);
                    break;
                case ExpressionOpCode.GreaterOrEqual:
                    BinaryComparison(stack, ref top, static (left, right) => left >= right);
                    break;
                case ExpressionOpCode.Equal:
                    BinaryEquality(stack, ref top, negate: false);
                    break;
                case ExpressionOpCode.NotEqual:
                    BinaryEquality(stack, ref top, negate: true);
                    break;
                case ExpressionOpCode.And:
                    BinaryBoolean(stack, ref top, static (left, right) => left && right);
                    break;
                case ExpressionOpCode.Or:
                    BinaryBoolean(stack, ref top, static (left, right) => left || right);
                    break;
                case ExpressionOpCode.JumpIfFalse:
                    if (!RequireBoolean(stack[top - 1]))
                    {
                        instructionIndex = instruction.Target - 1;
                    }
                    else
                    {
                        top--;
                    }

                    break;
                case ExpressionOpCode.JumpIfTrue:
                    if (RequireBoolean(stack[top - 1]))
                    {
                        instructionIndex = instruction.Target - 1;
                    }
                    else
                    {
                        top--;
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown expression instruction '{instruction.Code}'.");
            }
        }

        if (top != 1)
        {
            throw new InvalidOperationException("Compiled expression has an invalid stack result.");
        }

        return stack[0];
    }

    private static void BinaryNumber(
        Span<ExpressionValue> stack,
        ref int top,
        Func<double, double, double> operation)
    {
        var right = PopNumber(stack, ref top);
        var left = PopNumber(stack, ref top);
        stack[top++] = ExpressionValue.FromNumber(operation(left, right));
    }

    private static void BinaryComparison(
        Span<ExpressionValue> stack,
        ref int top,
        Func<double, double, bool> operation)
    {
        var right = PopNumber(stack, ref top);
        var left = PopNumber(stack, ref top);
        stack[top++] = ExpressionValue.FromBoolean(operation(left, right));
    }

    private static void BinaryBoolean(
        Span<ExpressionValue> stack,
        ref int top,
        Func<bool, bool, bool> operation)
    {
        var right = PopBoolean(stack, ref top);
        var left = PopBoolean(stack, ref top);
        stack[top++] = ExpressionValue.FromBoolean(operation(left, right));
    }

    private static void BinaryEquality(Span<ExpressionValue> stack, ref int top, bool negate)
    {
        var right = Pop(stack, ref top);
        var left = Pop(stack, ref top);
        if (left.IsBoolean != right.IsBoolean)
        {
            throw new ExpressionEvaluationException("Equality operands have different types.");
        }

        var equal = left.IsBoolean ? left.Boolean == right.Boolean : left.Number == right.Number;
        stack[top++] = ExpressionValue.FromBoolean(negate ? !equal : equal);
    }

    private static ExpressionValue Pop(Span<ExpressionValue> stack, ref int top) => stack[--top];

    private static bool RequireBoolean(ExpressionValue value) => value.IsBoolean
        ? value.Boolean
        : throw new ExpressionEvaluationException("Expected a boolean expression value.");

    private static double PopNumber(Span<ExpressionValue> stack, ref int top)
    {
        var value = Pop(stack, ref top);
        if (value.IsBoolean)
        {
            throw new ExpressionEvaluationException("Expected a numeric expression value.");
        }

        return value.Number;
    }

    private static bool PopBoolean(Span<ExpressionValue> stack, ref int top)
    {
        var value = Pop(stack, ref top);
        if (!value.IsBoolean)
        {
            throw new ExpressionEvaluationException("Expected a boolean expression value.");
        }

        return value.Boolean;
    }

    private static double Divide(double left, double right) => right == 0
        ? throw new ExpressionEvaluationException("Division by zero is not allowed.")
        : left / right;

    private static double Modulo(double left, double right) => right == 0
        ? throw new ExpressionEvaluationException("Modulo by zero is not allowed.")
        : left % right;

    private readonly record struct ExpressionValue(double Number, bool Boolean, bool IsBoolean)
    {
        public static ExpressionValue FromNumber(double number) => double.IsFinite(number)
            ? new(number, false, false)
            : throw new ExpressionEvaluationException("Expression evaluation produced a non-finite number.");

        public static ExpressionValue FromBoolean(bool boolean) => new(0, boolean, true);
    }
}

internal enum ExpressionOpCode
{
    Number,
    Boolean,
    ReadNumber,
    Count,
    Rate,
    EventCount,
    Negate,
    Not,
    Floor,
    Ceiling,
    Abs,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Minimum,
    Maximum,
    Clamp,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Equal,
    NotEqual,
    And,
    Or,
    JumpIfFalse,
    JumpIfTrue,
}

internal readonly record struct ExpressionInstruction(
    ExpressionOpCode Code,
    double Number = 0,
    bool Boolean = false,
    string? Text = null,
    int WindowTicks = 0,
    int Target = 0);

public static class ExpressionCompiler
{
    public const int MaximumSourceLength = 4096;
    public const int MaximumTokens = 256;
    public const int MaximumNesting = 32;
    public const int MaximumInstructions = 256;

    public static CompiledExpression Compile(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > MaximumSourceLength)
        {
            throw new ExpressionCompileException(
                $"Expression exceeds the {MaximumSourceLength}-character limit", MaximumSourceLength);
        }

        return new ExpressionParser(source).Compile();
    }

    private sealed class ExpressionParser
    {
        private readonly string _source;
        private readonly List<Token> _tokens = [];
        private readonly List<ExpressionInstruction> _instructions = [];
        private int _index;
        private int _nesting;
        private int _stackDepth;
        private int _maximumStackDepth;
        private readonly HashSet<string> _eventTypes = new(StringComparer.Ordinal);
        private int _maximumEventWindowTicks;

        public ExpressionParser(string source)
        {
            _source = source;
            Tokenize();
        }

        public CompiledExpression Compile()
        {
            var resultKind = ParseOr();
            if (Current.Kind != TokenKind.End)
            {
                throw Error("Unexpected token", Current.Position);
            }

            if (_stackDepth != 1)
            {
                throw Error("Expression is incomplete", _source.Length);
            }

            return new CompiledExpression([.. _instructions], resultKind, _maximumStackDepth,
                [.. _eventTypes.Order(StringComparer.Ordinal)], _maximumEventWindowTicks);
        }

        private ExpressionResultKind ParseOr()
        {
            var left = ParseAnd();
            while (Match(TokenKind.Or))
            {
                RequireKind(left, ExpressionResultKind.Boolean, "|| requires boolean operands");
                var jump = EmitJump(ExpressionOpCode.JumpIfTrue);
                var right = ParseAnd();
                RequireKind(right, ExpressionResultKind.Boolean, "|| requires boolean operands");
                PatchJump(jump);
                left = ExpressionResultKind.Boolean;
            }

            return left;
        }

        private ExpressionResultKind ParseAnd()
        {
            var left = ParseComparison();
            while (Match(TokenKind.And))
            {
                RequireKind(left, ExpressionResultKind.Boolean, "&& requires boolean operands");
                var jump = EmitJump(ExpressionOpCode.JumpIfFalse);
                var right = ParseComparison();
                RequireKind(right, ExpressionResultKind.Boolean, "&& requires boolean operands");
                PatchJump(jump);
                left = ExpressionResultKind.Boolean;
            }

            return left;
        }

        private ExpressionResultKind ParseComparison()
        {
            var left = ParseAdditive();
            if (Current.Kind is not (TokenKind.Less or TokenKind.LessOrEqual or TokenKind.Greater
                or TokenKind.GreaterOrEqual or TokenKind.Equal or TokenKind.NotEqual))
            {
                return left;
            }

            var operation = Take().Kind;
            var right = ParseAdditive();
            if (left != right)
            {
                throw Error("Comparison operands must have the same type", Previous.Position);
            }

            if (operation is not (TokenKind.Equal or TokenKind.NotEqual)
                && left != ExpressionResultKind.Number)
            {
                throw Error("Ordering comparisons require numeric operands", Previous.Position);
            }

            Emit(operation switch
            {
                TokenKind.Less => ExpressionOpCode.Less,
                TokenKind.LessOrEqual => ExpressionOpCode.LessOrEqual,
                TokenKind.Greater => ExpressionOpCode.Greater,
                TokenKind.GreaterOrEqual => ExpressionOpCode.GreaterOrEqual,
                TokenKind.Equal => ExpressionOpCode.Equal,
                _ => ExpressionOpCode.NotEqual,
            }, stackDelta: -1);
            return ExpressionResultKind.Boolean;
        }

        private ExpressionResultKind ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var operation = Take().Kind;
                var right = ParseMultiplicative();
                RequireNumeric(left, Previous.Position);
                RequireNumeric(right, Previous.Position);
                Emit(operation == TokenKind.Plus ? ExpressionOpCode.Add : ExpressionOpCode.Subtract, stackDelta: -1);
                left = ExpressionResultKind.Number;
            }

            return left;
        }

        private ExpressionResultKind ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
            {
                var operation = Take().Kind;
                var right = ParseUnary();
                RequireNumeric(left, Previous.Position);
                RequireNumeric(right, Previous.Position);
                Emit(operation switch
                {
                    TokenKind.Star => ExpressionOpCode.Multiply,
                    TokenKind.Slash => ExpressionOpCode.Divide,
                    _ => ExpressionOpCode.Modulo,
                }, stackDelta: -1);
                left = ExpressionResultKind.Number;
            }

            return left;
        }

        private ExpressionResultKind ParseUnary()
        {
            if (Match(TokenKind.Minus))
            {
                RequireNumeric(ParseUnary(), Previous.Position);
                Emit(ExpressionOpCode.Negate, stackDelta: 0);
                return ExpressionResultKind.Number;
            }

            if (Match(TokenKind.Not))
            {
                RequireKind(ParseUnary(), ExpressionResultKind.Boolean, "! requires a boolean operand");
                Emit(ExpressionOpCode.Not, stackDelta: 0);
                return ExpressionResultKind.Boolean;
            }

            return ParsePrimary();
        }

        private ExpressionResultKind ParsePrimary()
        {
            if (Match(TokenKind.Number))
            {
                Emit(new ExpressionInstruction(ExpressionOpCode.Number, Number: Previous.Number), stackDelta: 1);
                return ExpressionResultKind.Number;
            }

            if (Match(TokenKind.String))
            {
                throw Error("A string is only valid as a function argument", Previous.Position);
            }

            if (Match(TokenKind.LeftParen))
            {
                EnterNesting(Previous.Position);
                var result = ParseOr();
                Expect(TokenKind.RightParen, "Expected ')'");
                ExitNesting();
                return result;
            }

            if (Match(TokenKind.Identifier))
            {
                var identifier = Previous;
                if (identifier.Text == "true" || identifier.Text == "false")
                {
                    Emit(new ExpressionInstruction(ExpressionOpCode.Boolean, Boolean: identifier.Text == "true"), stackDelta: 1);
                    return ExpressionResultKind.Boolean;
                }

                if (!Match(TokenKind.LeftParen))
                {
                    Emit(new ExpressionInstruction(ExpressionOpCode.ReadNumber, Text: identifier.Text), stackDelta: 1);
                    return ExpressionResultKind.Number;
                }

                EnterNesting(identifier.Position);
                var result = ParseFunction(identifier);
                ExitNesting();
                return result;
            }

            throw Error("Expected a number, property, function, or parenthesized expression", Current.Position);
        }

        private ExpressionResultKind ParseFunction(Token function)
        {
            switch (function.Text)
            {
                case "count":
                case "rate":
                case "event_count":
                    var selector = Expect(TokenKind.String, $"{function.Text} requires a string selector");
                    if (string.IsNullOrWhiteSpace(selector.Text))
                    {
                        throw Error("Selector cannot be empty", selector.Position);
                    }

                    var window = 0;
                    if (function.Text != "count")
                    {
                        Expect(TokenKind.Comma, $"{function.Text} requires a window in ticks");
                        var windowToken = Expect(TokenKind.Number, "Window must be an integer number of ticks");
                        if (windowToken.Number < 1 || windowToken.Number > 1_000_000
                            || Math.Truncate(windowToken.Number) != windowToken.Number)
                        {
                            throw Error("Window must be between 1 and 1,000,000 whole ticks", windowToken.Position);
                        }

                        window = (int)windowToken.Number;
                    }

                    Expect(TokenKind.RightParen, "Expected ')' after function arguments");
                    if (function.Text != "count")
                    {
                        _eventTypes.Add(selector.Text);
                        _maximumEventWindowTicks = Math.Max(_maximumEventWindowTicks, window);
                    }

                    Emit(new ExpressionInstruction(function.Text switch
                    {
                        "count" => ExpressionOpCode.Count,
                        "rate" => ExpressionOpCode.Rate,
                        _ => ExpressionOpCode.EventCount,
                    }, Text: selector.Text, WindowTicks: window), stackDelta: 1);
                    return ExpressionResultKind.Number;
                case "floor":
                case "ceil":
                case "abs":
                    var single = ParseOr();
                    RequireNumeric(single, Previous.Position);
                    Expect(TokenKind.RightParen, "Expected ')' after function argument");
                    Emit(function.Text switch
                    {
                        "floor" => ExpressionOpCode.Floor,
                        "ceil" => ExpressionOpCode.Ceiling,
                        _ => ExpressionOpCode.Abs,
                    }, stackDelta: 0);
                    return ExpressionResultKind.Number;
                case "min":
                case "max":
                    var first = ParseOr();
                    RequireNumeric(first, Previous.Position);
                    Expect(TokenKind.Comma, $"{function.Text} requires two numeric arguments");
                    var second = ParseOr();
                    RequireNumeric(second, Previous.Position);
                    Expect(TokenKind.RightParen, "Expected ')' after function arguments");
                    Emit(function.Text == "min" ? ExpressionOpCode.Minimum : ExpressionOpCode.Maximum, stackDelta: -1);
                    return ExpressionResultKind.Number;
                case "clamp":
                    var value = ParseOr();
                    RequireNumeric(value, Previous.Position);
                    Expect(TokenKind.Comma, "clamp requires three numeric arguments");
                    var minimum = ParseOr();
                    RequireNumeric(minimum, Previous.Position);
                    Expect(TokenKind.Comma, "clamp requires three numeric arguments");
                    var maximum = ParseOr();
                    RequireNumeric(maximum, Previous.Position);
                    Expect(TokenKind.RightParen, "Expected ')' after function arguments");
                    Emit(ExpressionOpCode.Clamp, stackDelta: -2);
                    return ExpressionResultKind.Number;
                default:
                    throw Error($"Unknown function '{function.Text}'", function.Position);
            }
        }

        private void Tokenize()
        {
            var position = 0;
            while (position < _source.Length)
            {
                if (char.IsWhiteSpace(_source[position]))
                {
                    position++;
                    continue;
                }

                var start = position;
                var character = _source[position++];
                Token token;
                if (char.IsAsciiDigit(character) || character == '.' && position < _source.Length && char.IsAsciiDigit(_source[position]))
                {
                    while (position < _source.Length
                        && (char.IsAsciiDigit(_source[position]) || _source[position] == '.'))
                    {
                        position++;
                    }

                    var text = _source.AsSpan(start, position - start);
                    if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                        || !double.IsFinite(number))
                    {
                        throw Error("Invalid finite number", start);
                    }

                    token = new Token(TokenKind.Number, number, null, start);
                }
                else if (char.IsAsciiLetter(character) || character == '_')
                {
                    while (position < _source.Length
                        && (char.IsAsciiLetterOrDigit(_source[position]) || _source[position] is '_' or '.'))
                    {
                        position++;
                    }

                    token = new Token(TokenKind.Identifier, 0, _source[start..position], start);
                }
                else if (character is '\'' or '"')
                {
                    var value = new System.Text.StringBuilder();
                    var closed = false;
                    while (position < _source.Length)
                    {
                        var next = _source[position++];
                        if (next == character)
                        {
                            closed = true;
                            break;
                        }

                        if (next == '\\')
                        {
                            if (position >= _source.Length || _source[position] is not ('\\' or '\'' or '"'))
                            {
                                throw Error("Invalid string escape", position - 1);
                            }

                            next = _source[position++];
                        }

                        value.Append(next);
                    }

                    if (!closed)
                    {
                        throw Error("Unterminated string", start);
                    }

                    token = new Token(TokenKind.String, 0, value.ToString(), start);
                }
                else
                {
                    var kind = character switch
                    {
                        '(' => TokenKind.LeftParen,
                        ')' => TokenKind.RightParen,
                        ',' => TokenKind.Comma,
                        '+' => TokenKind.Plus,
                        '-' => TokenKind.Minus,
                        '*' => TokenKind.Star,
                        '/' => TokenKind.Slash,
                        '%' => TokenKind.Percent,
                        '!' when MatchNext('=') => TokenKind.NotEqual,
                        '!' => TokenKind.Not,
                        '<' when MatchNext('=') => TokenKind.LessOrEqual,
                        '<' => TokenKind.Less,
                        '>' when MatchNext('=') => TokenKind.GreaterOrEqual,
                        '>' => TokenKind.Greater,
                        '=' when MatchNext('=') => TokenKind.Equal,
                        '&' when MatchNext('&') => TokenKind.And,
                        '|' when MatchNext('|') => TokenKind.Or,
                        _ => throw Error($"Unsupported character '{character}'", start),
                    };
                    token = new Token(kind, 0, null, start);
                }

                _tokens.Add(token);
                if (_tokens.Count > MaximumTokens)
                {
                    throw Error($"Expression exceeds the {MaximumTokens}-token limit", start);
                }
            }

            _tokens.Add(new Token(TokenKind.End, 0, null, _source.Length));

            bool MatchNext(char expected)
            {
                if (position < _source.Length && _source[position] == expected)
                {
                    position++;
                    return true;
                }

                return false;
            }
        }

        private void Emit(ExpressionOpCode code, int stackDelta) =>
            Emit(new ExpressionInstruction(code), stackDelta);

        private int EmitJump(ExpressionOpCode code)
        {
            var index = _instructions.Count;
            Emit(new ExpressionInstruction(code), stackDelta: -1);
            return index;
        }

        private void PatchJump(int instructionIndex) =>
            _instructions[instructionIndex] = _instructions[instructionIndex] with { Target = _instructions.Count };

        private void Emit(ExpressionInstruction instruction, int stackDelta)
        {
            _instructions.Add(instruction);
            if (_instructions.Count > MaximumInstructions)
            {
                throw Error($"Expression exceeds the {MaximumInstructions}-instruction limit", Previous.Position);
            }

            _stackDepth += stackDelta;
            _maximumStackDepth = Math.Max(_maximumStackDepth, _stackDepth);
            if (_stackDepth > CompiledExpression.MaximumStackDepth)
            {
                throw Error($"Expression exceeds the {CompiledExpression.MaximumStackDepth}-value stack limit", Previous.Position);
            }
        }

        private void EnterNesting(int position)
        {
            if (++_nesting > MaximumNesting)
            {
                throw Error($"Expression exceeds the {MaximumNesting}-level nesting limit", position);
            }
        }

        private void ExitNesting() => _nesting--;

        private static void RequireNumeric(ExpressionResultKind kind, int position) =>
            RequireKind(kind, ExpressionResultKind.Number, "Operator requires numeric operands", position);

        private void RequireKind(ExpressionResultKind actual, ExpressionResultKind expected, string message) =>
            RequireKind(actual, expected, message, Previous.Position);

        private static void RequireKind(ExpressionResultKind actual, ExpressionResultKind expected, string message, int position)
        {
            if (actual != expected)
            {
                throw Error(message, position);
            }
        }

        private bool Match(TokenKind kind)
        {
            if (Current.Kind != kind)
            {
                return false;
            }

            _index++;
            return true;
        }

        private Token Expect(TokenKind kind, string message)
        {
            if (Current.Kind != kind)
            {
                throw Error(message, Current.Position);
            }

            return Take();
        }

        private Token Take() => _tokens[_index++];

        private Token Current => _tokens[_index];

        private Token Previous => _tokens[Math.Max(0, _index - 1)];

        private static ExpressionCompileException Error(string message, int position) => new(message, position);

        private readonly record struct Token(TokenKind Kind, double Number, string? Text, int Position);

        private enum TokenKind
        {
            End,
            Number,
            String,
            Identifier,
            LeftParen,
            RightParen,
            Comma,
            Plus,
            Minus,
            Star,
            Slash,
            Percent,
            Not,
            And,
            Or,
            Less,
            LessOrEqual,
            Greater,
            GreaterOrEqual,
            Equal,
            NotEqual,
        }
    }
}
