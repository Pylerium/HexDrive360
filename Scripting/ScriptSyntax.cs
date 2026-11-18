using System.Globalization;
using System.Text;

namespace MemoryEditor.Scripting;

// ════════════════════════════════════════════════════════════════════════════
//  XScript – lexer, AST and parser
//  A small C-like language that stays backward compatible with the original
//  line-based script format (wait 100 / log "x" / $vars / for(i=0;i<n;i++)).
// ════════════════════════════════════════════════════════════════════════════

public sealed class ScriptError : Exception
{
    public int Line { get; }
    public int Column { get; }
    public bool IsWarning { get; init; }
    public ScriptError(string message, int line, int column) : base(message)
    {
        Line = line;
        Column = column;
    }
    public override string ToString() => $"Line {Line}, col {Column}: {Message}";
}

public enum TokenKind
{
    Number, String, Ident, Keyword, Op, Newline, EOF
}

public readonly record struct Token(TokenKind Kind, string Text, object? Value, int Line, int Col, int Pos)
{
    public bool Is(string text) => (Kind == TokenKind.Op || Kind == TokenKind.Keyword) && Text == text;
    public override string ToString() => Kind == TokenKind.EOF ? "end of script" : Kind == TokenKind.Newline ? "end of line" : $"'{Text}'";
}

public static class ScriptLexer
{
    public static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "var", "let", "const", "if", "else", "while", "for", "in", "loop", "repeat", "break", "continue",
        "func", "function", "return", "true", "false", "null", "and", "or", "not"
    };

    private static readonly string[] Ops =
    {
        "<<=", ">>=", "...", "..",
        "==", "!=", "<=", ">=", "&&", "||", "<<", ">>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "++", "--",
        "+", "-", "*", "/", "%", "=", "<", ">", "!", "~", "&", "|", "^", "(", ")", "{", "}", "[", "]", ",", ";", "?", ":"
    };

    public static List<Token> Tokenize(string src, List<ScriptError>? errors = null)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, col = 1, depth = 0;

        void Advance(int n = 1)
        {
            for (int k = 0; k < n && i < src.Length; k++)
            {
                if (src[i] == '\n') { line++; col = 1; } else col++;
                i++;
            }
        }

        while (i < src.Length)
        {
            char c = src[i];

            if (c == '\n')
            {
                if (depth == 0 && tokens.Count > 0 && tokens[^1].Kind != TokenKind.Newline)
                    tokens.Add(new Token(TokenKind.Newline, "\n", null, line, col, i));
                Advance();
                continue;
            }
            if (char.IsWhiteSpace(c)) { Advance(); continue; }

            // comments
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') Advance();
                continue;
            }
            if (c == '#')
            {
                while (i < src.Length && src[i] != '\n') Advance();
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                Advance(2);
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/')) Advance();
                Advance(2);
                continue;
            }

            int sl = line, sc = col, sp = i;

            // numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1]) && !(tokens.Count > 0 && tokens[^1].Is(".."))))
            {
                if (c == '0' && i + 1 < src.Length && (src[i + 1] == 'x' || src[i + 1] == 'X'))
                {
                    Advance(2);
                    int st = i;
                    while (i < src.Length && (Uri.IsHexDigit(src[i]) || src[i] == '_')) Advance();
                    var hex = src[st..i].Replace("_", "");
                    if (hex.Length == 0 || !ulong.TryParse(hex, NumberStyles.HexNumber, null, out var hv))
                    {
                        errors?.Add(new ScriptError("Invalid hex literal", sl, sc));
                        hv = 0;
                    }
                    tokens.Add(new Token(TokenKind.Number, src[sp..i], unchecked((long)hv), sl, sc, sp));
                    continue;
                }
                if (c == '0' && i + 1 < src.Length && (src[i + 1] == 'b' || src[i + 1] == 'B') && i + 2 < src.Length && (src[i + 2] == '0' || src[i + 2] == '1'))
                {
                    Advance(2);
                    int st = i;
                    while (i < src.Length && (src[i] == '0' || src[i] == '1' || src[i] == '_')) Advance();
                    var bin = src[st..i].Replace("_", "");
                    tokens.Add(new Token(TokenKind.Number, src[sp..i], Convert.ToInt64(bin, 2), sl, sc, sp));
                    continue;
                }
                bool isFloat = false;
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '_')) Advance();
                if (i < src.Length && src[i] == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1]))
                {
                    isFloat = true;
                    Advance();
                    while (i < src.Length && char.IsDigit(src[i])) Advance();
                }
                if (i < src.Length && (src[i] == 'e' || src[i] == 'E') && i + 1 < src.Length &&
                    (char.IsDigit(src[i + 1]) || ((src[i + 1] == '-' || src[i + 1] == '+') && i + 2 < src.Length && char.IsDigit(src[i + 2]))))
                {
                    isFloat = true;
                    Advance(2);
                    while (i < src.Length && char.IsDigit(src[i])) Advance();
                }
                var text = src[sp..i].Replace("_", "");
                if (i < src.Length && (src[i] == 'f' || src[i] == 'F') && !(i + 1 < src.Length && char.IsLetterOrDigit(src[i + 1])))
                {
                    isFloat = true;
                    Advance();
                }
                object val = isFloat
                    ? double.Parse(text, CultureInfo.InvariantCulture)
                    : (long.TryParse(text, out var lv) ? lv : (object)double.Parse(text, CultureInfo.InvariantCulture));
                tokens.Add(new Token(TokenKind.Number, src[sp..i], val, sl, sc, sp));
                continue;
            }

            // strings
            if (c == '"' || c == '\'')
            {
                char q = c;
                Advance();
                var sb = new StringBuilder();
                bool closed = false;
                while (i < src.Length)
                {
                    char ch = src[i];
                    if (ch == '\n') break;
                    if (ch == q) { Advance(); closed = true; break; }
                    if (ch == '\\' && i + 1 < src.Length)
                    {
                        char e = src[i + 1];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => e });
                        if (e is not ('n' or 't' or 'r' or '0' or '"' or '\'' or '\\')) sb.Insert(sb.Length - 1, '\\');
                        Advance(2);
                        continue;
                    }
                    sb.Append(ch);
                    Advance();
                }
                if (!closed) errors?.Add(new ScriptError("Unterminated string literal", sl, sc));
                tokens.Add(new Token(TokenKind.String, src[sp..i], sb.ToString(), sl, sc, sp));
                continue;
            }

            // identifiers ($name is the same as name)
            if (char.IsLetter(c) || c == '_' || c == '$' || c == '@')
            {
                if (c == '$' || c == '@') Advance();
                int st = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) Advance();
                var word = src[st..i];
                if (word.Length == 0)
                {
                    errors?.Add(new ScriptError($"Unexpected character '{c}'", sl, sc));
                    continue;
                }
                bool sigil = st != sp;
                var kind = !sigil && Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Ident;
                tokens.Add(new Token(kind, word, null, sl, sc, sp));
                continue;
            }

            // operators
            string? op = null;
            foreach (var o in Ops)
            {
                if (string.CompareOrdinal(src, i, o, 0, o.Length) == 0) { op = o; break; }
            }
            if (op != null)
            {
                if (op is "(" or "[") depth++;
                else if (op is ")" or "]") depth = Math.Max(0, depth - 1);
                tokens.Add(new Token(TokenKind.Op, op, null, sl, sc, sp));
                Advance(op.Length);
                continue;
            }

            errors?.Add(new ScriptError($"Unexpected character '{c}'", sl, sc));
            Advance();
        }

        tokens.Add(new Token(TokenKind.EOF, "", null, line, col, src.Length));
        return tokens;
    }
}

// ───────────────────────────── AST ─────────────────────────────

public abstract class Node
{
    public int Line { get; init; }
    public int Col { get; init; }
}

public abstract class Expr : Node { }
public sealed class LiteralExpr : Expr { public object? Value { get; init; } public bool Interpolate { get; init; } }
public sealed class IdentExpr : Expr { public string Name { get; init; } = ""; }
public sealed class UnaryExpr : Expr { public string Op { get; init; } = ""; public Expr Operand { get; init; } = null!; }
public sealed class BinaryExpr : Expr { public string Op { get; init; } = ""; public Expr Left { get; init; } = null!; public Expr Right { get; init; } = null!; }
public sealed class TernaryExpr : Expr { public Expr Cond { get; init; } = null!; public Expr Then { get; init; } = null!; public Expr Else { get; init; } = null!; }
public sealed class CallExpr : Expr { public string Name { get; init; } = ""; public List<Expr> Args { get; init; } = new(); }
public sealed class IndexExpr : Expr { public Expr Target { get; init; } = null!; public Expr Index { get; init; } = null!; }
public sealed class ArrayExpr : Expr { public List<Expr> Items { get; init; } = new(); }
public sealed class AssignExpr : Expr { public string Name { get; init; } = ""; public Expr? IndexTarget { get; init; } public Expr? Index { get; init; } public string Op { get; init; } = "="; public Expr Value { get; init; } = null!; }
public sealed class IncDecExpr : Expr { public string Name { get; init; } = ""; public bool Increment { get; init; } public bool Prefix { get; init; } }

public abstract class Stmt : Node { }
public sealed class ExprStmt : Stmt { public Expr Expr { get; init; } = null!; }
public sealed class VarStmt : Stmt { public string Name { get; init; } = ""; public Expr? Init { get; init; } }
public sealed class BlockStmt : Stmt { public List<Stmt> Body { get; init; } = new(); }
public sealed class IfStmt : Stmt { public Expr Cond { get; init; } = null!; public Stmt Then { get; init; } = null!; public Stmt? Else { get; init; } }
public sealed class WhileStmt : Stmt { public Expr? Cond { get; init; } public Stmt Body { get; init; } = null!; }
public sealed class RepeatStmt : Stmt { public Expr Count { get; init; } = null!; public Stmt Body { get; init; } = null!; }
public sealed class ForStmt : Stmt { public Stmt? Init { get; init; } public Expr? Cond { get; init; } public Expr? Step { get; init; } public Stmt Body { get; init; } = null!; }
public sealed class ForInStmt : Stmt { public string Var { get; init; } = ""; public Expr From { get; init; } = null!; public Expr? To { get; init; } public bool Inclusive { get; init; } public Stmt Body { get; init; } = null!; }
public sealed class BreakStmt : Stmt { }
public sealed class ContinueStmt : Stmt { }
public sealed class ReturnStmt : Stmt { public Expr? Value { get; init; } }
public sealed class FuncStmt : Stmt { public string Name { get; init; } = ""; public List<string> Params { get; init; } = new(); public BlockStmt Body { get; init; } = null!; }

public sealed class ScriptProgram
{
    public List<Stmt> Body { get; } = new();
    public Dictionary<string, FuncStmt> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ScriptError> Errors { get; } = new();
    public HashSet<string> DeclaredVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
}

// ───────────────────────────── Parser ─────────────────────────────

public sealed class ScriptParser
{
    private readonly List<Token> _t;
    private int _p;
    private readonly ScriptProgram _prog = new();
    private readonly Func<string, bool> _isCommand;

    /// <param name="isCommand">Returns true for names that may be called without parentheses (wait 100, log "x").</param>
    private ScriptParser(List<Token> tokens, Func<string, bool> isCommand)
    {
        _t = tokens;
        _isCommand = isCommand;
    }

    public static ScriptProgram Parse(string source, Func<string, bool>? isCommand = null)
    {
        var lexErrors = new List<ScriptError>();
        var tokens = ScriptLexer.Tokenize(source.Replace("\r\n", "\n").Replace('\r', '\n'), lexErrors);
        var parser = new ScriptParser(tokens, isCommand ?? (_ => false));
        parser._prog.Errors.AddRange(lexErrors);
        parser.ParseProgram();
        return parser._prog;
    }

    private Token Cur => _t[_p];
    private Token Peek(int n = 1) => _t[Math.Min(_p + n, _t.Count - 1)];
    private Token Next() { var t = _t[_p]; if (_p < _t.Count - 1) _p++; return t; }
    private bool At(string op) => Cur.Is(op);
    private bool Accept(string op) { if (At(op)) { Next(); return true; } return false; }

    private Token Expect(string op, string context)
    {
        if (At(op)) return Next();
        throw new ScriptError($"Expected '{op}' {context} but found {Cur}", Cur.Line, Cur.Col);
    }

    private void SkipNewlines()
    {
        while (Cur.Kind == TokenKind.Newline || At(";")) Next();
    }

    private void ParseProgram()
    {
        while (true)
        {
            SkipNewlines();
            if (Cur.Kind == TokenKind.EOF) break;
            try
            {
                var s = ParseStatement();
                if (s != null) _prog.Body.Add(s);
            }
            catch (ScriptError e)
            {
                _prog.Errors.Add(e);
                Recover();
            }
        }
    }

    private void Recover()
    {
        // skip to the end of the line, keeping brace structure sane
        int startLine = Cur.Line;
        while (Cur.Kind != TokenKind.EOF && Cur.Kind != TokenKind.Newline && !At(";"))
        {
            if (At("}") && Cur.Line != startLine) break;
            Next();
        }
    }

    private void EndStatement()
    {
        if (At(";")) { Next(); return; }
        if (Cur.Kind == TokenKind.Newline) { Next(); return; }
        if (At("}") || Cur.Kind == TokenKind.EOF) return;
        throw new ScriptError($"Unexpected {Cur} – expected end of statement", Cur.Line, Cur.Col);
    }

    private BlockStmt ParseBlock()
    {
        var open = Expect("{", "to open block");
        var block = new BlockStmt { Line = open.Line, Col = open.Col };
        while (true)
        {
            SkipNewlines();
            if (At("}")) { Next(); break; }
            if (Cur.Kind == TokenKind.EOF)
                throw new ScriptError("Missing '}' – block opened here is never closed", open.Line, open.Col);
            try
            {
                var s = ParseStatement();
                if (s != null) block.Body.Add(s);
            }
            catch (ScriptError e)
            {
                _prog.Errors.Add(e);
                Recover();
            }
        }
        return block;
    }

    private Stmt ParseBody()
    {
        while (Cur.Kind == TokenKind.Newline) Next();
        if (At("{")) return ParseBlock();
        return ParseStatement() ?? new BlockStmt { Line = Cur.Line, Col = Cur.Col };
    }

    private Expr ParseCondition()
    {
        // parentheses are optional: if x > 3 { } and if (x > 3) { } both work
        return ParseExpr();
    }

    private Stmt? ParseStatement()
    {
        var t = Cur;
        if (t.Kind == TokenKind.Keyword)
        {
            switch (t.Text)
            {
                case "var" when !Peek().Is("("):
                case "let":
                case "const":
                {
                    Next();
                    var name = Cur;
                    if (name.Kind != TokenKind.Ident) throw new ScriptError($"Expected variable name after '{t.Text}'", name.Line, name.Col);
                    Next();
                    Expr? init = null;
                    if (Accept("=")) init = ParseExpr();
                    _prog.DeclaredVariables.Add(name.Text);
                    EndStatement();
                    return new VarStmt { Name = name.Text, Init = init, Line = t.Line, Col = t.Col };
                }
                case "if":
                {
                    Next();
                    var cond = ParseCondition();
                    var then = ParseBody();
                    Stmt? els = null;
                    // look past newlines for else
                    int save = _p;
                    while (Cur.Kind == TokenKind.Newline || At(";")) Next();
                    if (Cur.Is("else"))
                    {
                        Next();
                        if (Cur.Is("if")) els = ParseStatement();
                        else els = ParseBody();
                    }
                    else _p = save;
                    return new IfStmt { Cond = cond, Then = then, Else = els, Line = t.Line, Col = t.Col };
                }
                case "while":
                {
                    Next();
                    var cond = ParseCondition();
                    var body = ParseBody();
                    return new WhileStmt { Cond = cond, Body = body, Line = t.Line, Col = t.Col };
                }
                case "loop":
                {
                    Next();
                    var body = ParseBody();
                    return new WhileStmt { Cond = null, Body = body, Line = t.Line, Col = t.Col };
                }
                case "repeat":
                {
                    Next();
                    var count = ParseExpr();
                    var body = ParseBody();
                    return new RepeatStmt { Count = count, Body = body, Line = t.Line, Col = t.Col };
                }
                case "for":
                    return ParseFor();
                case "break":
                    Next(); EndStatement();
                    return new BreakStmt { Line = t.Line, Col = t.Col };
                case "continue":
                    Next(); EndStatement();
                    return new ContinueStmt { Line = t.Line, Col = t.Col };
                case "return":
                {
                    Next();
                    Expr? v = null;
                    if (Cur.Kind != TokenKind.Newline && !At(";") && !At("}") && Cur.Kind != TokenKind.EOF) v = ParseExpr();
                    EndStatement();
                    return new ReturnStmt { Value = v, Line = t.Line, Col = t.Col };
                }
                case "func":
                case "function":
                {
                    Next();
                    var name = Cur;
                    if (name.Kind != TokenKind.Ident) throw new ScriptError("Expected function name", name.Line, name.Col);
                    Next();
                    Expect("(", "after function name");
                    var ps = new List<string>();
                    while (!At(")"))
                    {
                        var p = Next();
                        if (p.Kind != TokenKind.Ident) throw new ScriptError("Expected parameter name", p.Line, p.Col);
                        ps.Add(p.Text);
                        if (!Accept(",")) break;
                    }
                    Expect(")", "to close parameter list");
                    while (Cur.Kind == TokenKind.Newline) Next();
                    var body = ParseBlock();
                    var f = new FuncStmt { Name = name.Text, Params = ps, Body = body, Line = t.Line, Col = t.Col };
                    _prog.Functions[name.Text] = f;
                    return f;
                }
            }
        }

        if (At("{")) return ParseBlock();

        // command-style call: wait 100 / log "hello" / walk 0x8200, 0x10
        if (t.Kind == TokenKind.Ident && _isCommand(t.Text))
        {
            var n = Peek();
            bool startsExpr = n.Kind is TokenKind.Number or TokenKind.String or TokenKind.Ident ||
                              (n.Kind == TokenKind.Keyword && n.Text is "true" or "false" or "null" or "not") ||
                              n.Is("-") || n.Is("!") || n.Is("[") || n.Is("~");
            if (startsExpr)
            {
                Next();
                var args = new List<Expr> { ParseExpr() };
                while (Accept(",")) args.Add(ParseExpr());
                EndStatement();
                return new ExprStmt { Expr = new CallExpr { Name = t.Text, Args = args, Line = t.Line, Col = t.Col }, Line = t.Line, Col = t.Col };
            }
        }

        var e = ParseExpr();
        EndStatement();
        return new ExprStmt { Expr = e, Line = t.Line, Col = t.Col };
    }

    private Stmt ParseFor()
    {
        var t = Next(); // for
        // for i in 0..10 { }   /  for (i in 0..10)
        bool paren = At("(");
        if ((paren && Peek().Kind == TokenKind.Ident && Peek(2).Is("in")) || (Cur.Kind == TokenKind.Ident && Peek().Is("in")))
        {
            if (paren) Next();
            var v = Next();
            Next(); // in
            var from = ParseExpr();
            Expr? to = null;
            bool inclusive = false;
            if (At("..") || At("..."))
            {
                inclusive = Next().Text == "...";
                to = ParseExpr();
            }
            if (paren) Expect(")", "to close for");
            var body = ParseBody();
            return new ForInStmt { Var = v.Text, From = from, To = to, Inclusive = inclusive, Body = body, Line = t.Line, Col = t.Col };
        }

        Expect("(", "after 'for'");
        Stmt? init = null;
        if (!At(";"))
        {
            if (Cur.Is("var") || Cur.Is("let"))
            {
                Next();
                var name = Next();
                Expr? iv = null;
                if (Accept("=")) iv = ParseExpr();
                init = new VarStmt { Name = name.Text, Init = iv, Line = name.Line, Col = name.Col };
            }
            else init = new ExprStmt { Expr = ParseExpr(), Line = Cur.Line, Col = Cur.Col };
        }
        Expect(";", "in for header");
        Expr? cond = At(";") ? null : ParseExpr();
        Expect(";", "in for header");
        Expr? step = At(")") ? null : ParseExpr();
        Expect(")", "to close for header");
        var b = ParseBody();
        return new ForStmt { Init = init, Cond = cond, Step = step, Body = b, Line = t.Line, Col = t.Col };
    }

    // ── expressions ──

    private Expr ParseExpr() => ParseAssignment();

    private static readonly HashSet<string> AssignOps = new() { "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=" };

    private Expr ParseAssignment()
    {
        var left = ParseTernary();
        if (Cur.Kind == TokenKind.Op && AssignOps.Contains(Cur.Text))
        {
            var op = Next();
            var value = ParseAssignment();
            if (left is IdentExpr id)
                return new AssignExpr { Name = id.Name, Op = op.Text, Value = value, Line = left.Line, Col = left.Col };
            if (left is IndexExpr ix)
                return new AssignExpr { IndexTarget = ix.Target, Index = ix.Index, Op = op.Text, Value = value, Line = left.Line, Col = left.Col };
            throw new ScriptError("Left side of assignment must be a variable", op.Line, op.Col);
        }
        return left;
    }

    private Expr ParseTernary()
    {
        var c = ParseBinary(0);
        if (At("?"))
        {
            var q = Next();
            var a = ParseAssignment();
            Expect(":", "in conditional expression");
            var b = ParseAssignment();
            return new TernaryExpr { Cond = c, Then = a, Else = b, Line = q.Line, Col = q.Col };
        }
        return c;
    }

    private static readonly string[][] Levels =
    {
        new[] { "||", "or" },
        new[] { "&&", "and" },
        new[] { "|" },
        new[] { "^" },
        new[] { "&" },
        new[] { "==", "!=" },
        new[] { "<", ">", "<=", ">=" },
        new[] { "<<", ">>" },
        new[] { "+", "-" },
        new[] { "*", "/", "%" },
    };

    private Expr ParseBinary(int level)
    {
        if (level >= Levels.Length) return ParseUnary();
        var left = ParseBinary(level + 1);
        while ((Cur.Kind == TokenKind.Op || Cur.Kind == TokenKind.Keyword) && Array.IndexOf(Levels[level], Cur.Text) >= 0)
        {
            var op = Next();
            var right = ParseBinary(level + 1);
            var norm = op.Text switch { "or" => "||", "and" => "&&", _ => op.Text };
            left = new BinaryExpr { Op = norm, Left = left, Right = right, Line = op.Line, Col = op.Col };
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (At("-") || At("!") || At("~") || Cur.Is("not") || At("+"))
        {
            var op = Next();
            var operand = ParseUnary();
            if (op.Text == "+") return operand;
            return new UnaryExpr { Op = op.Text == "not" ? "!" : op.Text, Operand = operand, Line = op.Line, Col = op.Col };
        }
        if (At("++") || At("--"))
        {
            var op = Next();
            var id = Next();
            if (id.Kind != TokenKind.Ident) throw new ScriptError($"'{op.Text}' must be followed by a variable", id.Line, id.Col);
            return new IncDecExpr { Name = id.Text, Increment = op.Text == "++", Prefix = true, Line = op.Line, Col = op.Col };
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var e = ParsePrimary();
        while (true)
        {
            if (At("["))
            {
                var b = Next();
                var idx = ParseExpr();
                Expect("]", "to close index");
                e = new IndexExpr { Target = e, Index = idx, Line = b.Line, Col = b.Col };
                continue;
            }
            if ((At("++") || At("--")) && e is IdentExpr id)
            {
                var op = Next();
                e = new IncDecExpr { Name = id.Name, Increment = op.Text == "++", Prefix = false, Line = id.Line, Col = id.Col };
                continue;
            }
            break;
        }
        return e;
    }

    private Expr ParsePrimary()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Next();
                return new LiteralExpr { Value = t.Value, Line = t.Line, Col = t.Col };
            case TokenKind.String:
                Next();
                return new LiteralExpr { Value = t.Value, Interpolate = ((string)t.Value!).Contains('$'), Line = t.Line, Col = t.Col };
            case TokenKind.Keyword when t.Text is "true" or "false":
                Next();
                return new LiteralExpr { Value = t.Text == "true", Line = t.Line, Col = t.Col };
            case TokenKind.Keyword when t.Text == "null":
                Next();
                return new LiteralExpr { Value = null, Line = t.Line, Col = t.Col };
            case TokenKind.Keyword when t.Text == "var" && Peek().Is("("):
            case TokenKind.Ident:
            {
                Next();
                if (At("("))
                {
                    Next();
                    var args = new List<Expr>();
                    while (Cur.Kind == TokenKind.Newline) Next();
                    if (!At(")"))
                    {
                        do
                        {
                            while (Cur.Kind == TokenKind.Newline) Next();
                            args.Add(ParseExpr());
                        } while (Accept(","));
                    }
                    Expect(")", $"to close call to {t.Text}()");
                    return new CallExpr { Name = t.Text, Args = args, Line = t.Line, Col = t.Col };
                }
                return new IdentExpr { Name = t.Text, Line = t.Line, Col = t.Col };
            }
        }

        if (At("("))
        {
            Next();
            var e = ParseExpr();
            Expect(")", "to close parenthesis");
            return e;
        }
        if (At("["))
        {
            var b = Next();
            var items = new List<Expr>();
            if (!At("]"))
            {
                do { items.Add(ParseExpr()); } while (Accept(","));
            }
            Expect("]", "to close array");
            return new ArrayExpr { Items = items, Line = b.Line, Col = b.Col };
        }

        throw new ScriptError($"Unexpected {t}", t.Line, t.Col);
    }
}
