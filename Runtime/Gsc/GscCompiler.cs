using Irony.Parsing;

namespace MemoryEditor.Runtime.Gsc;

/// <summary>One compiler diagnostic (1-based line / column).</summary>
public sealed record GscDiagnostic(int Line, int Column, string Message, bool IsError);

public sealed record GscCompileResult(byte[]? Output, IReadOnlyList<GscDiagnostic> Diagnostics, TimeSpan Elapsed)
{
    public bool Success => Output != null && Diagnostics.All(d => !d.IsError);
}

/// <summary>
/// GSC source → Black Ops II console bytecode, using the T6 compiler (Irony grammar + bytecode writer) that ships with
/// Project TCM / GSC Studio-style tools. The compiler writes its output next to its input, so every compile runs on a
/// private temp copy – the user's source file is never touched (given a .gsc path it would overwrite it in place).
/// </summary>
public static class GscCompiler
{
    private static readonly object Gate = new();
    private static LanguageData? _language;

    /// <summary>Builds the grammar once in the background so the first compile is quick.</summary>
    public static void Warmup() => Task.Run(() => { try { lock (Gate) Language(); } catch { } });

    private static LanguageData Language() => _language ??= new LanguageData(new Compiler.GSCGrammar());

    /// <summary>Syntax check only (fast, no output).</summary>
    public static IReadOnlyList<GscDiagnostic> Check(string source)
    {
        lock (Gate)
        {
            var tree = new Parser(Language()).Parse(Normalize(source), "script");
            return Diagnostics(tree).ToList();
        }
    }

    public static Task<GscCompileResult> CompileAsync(string source, CancellationToken ct = default) => Task.Run(() => Compile(source), ct);

    public static GscCompileResult Compile(string source)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (Gate)
        {
            var text = Normalize(source);
            var tree = new Parser(Language()).Parse(text, "script");
            var diags = Diagnostics(tree).ToList();
            if (tree.Status == ParseTreeStatus.Error || tree.Root == null)
            {
                if (diags.Count == 0) diags.Add(new GscDiagnostic(1, 1, "The script could not be parsed", true));
                return new GscCompileResult(null, diags, sw.Elapsed);
            }

            var dir = Path.Combine(Path.GetTempPath(), "MemoryEditor-gsc", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var input = Path.Combine(dir, "script.txt");      // the compiler maps *.txt → *.gsc
                File.WriteAllText(input, text);
                bool ok;
                try { ok = new Compiler.ScriptCompiler(tree, input).Init(); }
                catch (Exception ex)
                {
                    // the compiler reports semantic problems (unknown builtin, bad call…) by throwing
                    diags.Add(new GscDiagnostic(1, 1, $"Compiler error: {Inner(ex).Message}", true));
                    return new GscCompileResult(null, diags, sw.Elapsed);
                }
                var output = Path.Combine(dir, "script.gsc");
                if (!File.Exists(output))
                {
                    diags.Add(new GscDiagnostic(1, 1, ok ? "The compiler produced no output" : "Compilation failed (no details from the compiler)", true));
                    return new GscCompileResult(null, diags, sw.Elapsed);
                }
                var bytes = File.ReadAllBytes(output);
                var parsed = GscBinary.TryParse(bytes);
                if (parsed == null || !parsed.IsValid)
                {
                    diags.Add(new GscDiagnostic(1, 1, "Compiler output is not a valid console script: " + string.Join("; ", parsed?.Problems ?? new[] { "bad header" }), true));
                    return new GscCompileResult(null, diags, sw.Elapsed);
                }
                return new GscCompileResult(bytes, diags, sw.Elapsed);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    private static Exception Inner(Exception ex) => ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;

    /// <summary>The grammar is picky about line endings and a BOM.</summary>
    private static string Normalize(string s) => s.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private static IEnumerable<GscDiagnostic> Diagnostics(ParseTree tree)
    {
        foreach (var m in tree.ParserMessages)
            yield return new GscDiagnostic(m.Location.Line + 1, m.Location.Column + 1, m.Message, m.Level == Irony.ErrorLevel.Error);
    }
}
