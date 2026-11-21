// hot-logic-walker: Roslyn syntax walker that flags loop and logic bug classes in decompiled EA C#,
// restricted to a hot-method list (docs/604). Output TSV goes wherever the caller says (keep it in
// game-copy/ or a scratchpad: it quotes EA method names and line numbers).
//
// usage: dotnet run -- <decompiled-root> <hotlist.tsv> <out.tsv> [--all]
//   hotlist.tsv: "<Namespace.Type[.Nested]>\t<method>" per line (get_X / set_X = property accessors)
//   --all: ignore the hot list and scan every method
//   --files <list>: scan every method of the listed files instead (one path per line, relative to the root)
// also writes <out.tsv>.ranges: file, first line, last line, method of every scanned method
//
// Classes: INFLOOP, MODENUM, FWDREMOVE, FIRSTEXIT, NESTEDSCAN, WRONGVAR, OFFBYONE, NULLAFTERDEREF,
//          EVENTNOREMOVE, REPEATCALL. Heuristics are syntax-only; every hit needs a hand check.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class P
{
    // calls that walk a world, lot or household population (expensive, O(n) or worse)
    static readonly HashSet<string> Scan = new() {
        "GetObjects", "GetObjectsInRadius", "GetObjectsOnLot", "GetSims", "GetAllActors", "GetActorsOfType",
        "GetObjectsOfType", "FindObjects", "GetAllObjectsOnLot", "GetObjectsInRoom", "AllActors", "AllSims",
        "AllLots", "Actors", "GetSimsOnLot", "GetAllSims", "GetNearbySims", "GetSimsInRadius", "GetLotsOfType",
        "GetObjectsWithinRadius", "GetSimDescriptions", "AllSimDescriptions", "GetRabbitHolesOfType",
        "GetInRadiusSims", "GetAllHumanSims", "GetSimsAtLot", "AllHouseholds", "sHouseholdList" };
    static readonly HashSet<string> Mutators = new() { "Remove", "RemoveAt", "Add", "Insert", "Clear", "AddRange", "RemoveAll", "RemoveRange" };
    static readonly HashSet<string> Yields = new() { "Sleep", "Yield", "YieldThread", "WaitForMs", "DoLoop", "Wait" };

    static readonly List<string[]> Hits = new();
    static string File_ = "";
    static string Meth = "";
    static SyntaxTree Tree;
    static readonly List<string> Ranges = new();   // hot method line ranges, for filtering other scanners' output
    static readonly HashSet<string> Removed = new();   // handler names seen in any "-="
    static readonly List<(string file, string meth, int line, string handler, string target)> Adds = new();

    static void Hit(string cls, SyntaxNode n, string detail)
    {
        var line = n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        Hits.Add(new[] { cls, File_, line.ToString(), Meth, detail.Replace('\t', ' ').Replace('\n', ' ') });
    }

    static int Main(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("usage: <decompiled-root> <hotlist.tsv> <out.tsv> [--all]"); return 2; }
        bool all = a.Contains("--all");
        int fi = Array.IndexOf(a, "--files");   // --files <list>: every method in the listed files (paths relative to the root)
        HashSet<string> onlyFiles = fi >= 0 && fi + 1 < a.Length ? new HashSet<string>(File.ReadLines(a[fi + 1]).Select(x => x.Trim()).Where(x => x.Length > 0)) : null;
        if (onlyFiles != null) all = true;
        var hot = new HashSet<string>();
        foreach (var l in File.ReadLines(a[1]))
        {
            var p = l.Split('\t'); if (p.Length < 2) continue;
            var m = p[1];
            if (m.StartsWith("get_") || m.StartsWith("set_") || m.StartsWith("add_") || m.StartsWith("remove_")) m = m.Substring(m.IndexOf('_') + 1);
            if (m == ".ctor") m = p[0].Split('.').Last();
            hot.Add(p[0] + "\t" + m);
        }
        var files = Directory.GetFiles(a[0], "*.cs", SearchOption.AllDirectories);
        var trees = files.AsParallel().Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)).ToList();
        foreach (var t in trees)   // pass 1: every "-=" handler in the code base
            foreach (var ae in t.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (ae.IsKind(SyntaxKind.SubtractAssignmentExpression)) Removed.Add(HandlerName(ae.Right));
        int scanned = 0;
        foreach (var t in trees)
        {
            Tree = t; File_ = Path.GetRelativePath(a[0], t.FilePath);
            if (onlyFiles != null && !onlyFiles.Contains(File_)) continue;
            foreach (var md in t.GetRoot().DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax || n is AccessorDeclarationSyntax))
            {
                var (type, name) = Owner(md);
                if (type == null) continue;
                if (!all && !hot.Contains(type + "\t" + name)) continue;
                Meth = type + "." + name; scanned++;
                var span = md.GetLocation().GetLineSpan();
                Ranges.Add($"{File_}\t{span.StartLinePosition.Line + 1}\t{span.EndLinePosition.Line + 1}\t{Meth}");
                SyntaxNode body = md is BaseMethodDeclarationSyntax b ? (SyntaxNode)b.Body ?? b.ExpressionBody : ((AccessorDeclarationSyntax)md).Body ?? (SyntaxNode)((AccessorDeclarationSyntax)md).ExpressionBody;
                if (body == null) continue;
                Walk(body);
            }
        }
        foreach (var x in Adds)
            if (!Removed.Contains(x.handler)) Hits.Add(new[] { "EVENTNOREMOVE", x.file, x.line.ToString(), x.meth, $"{x.target} += {x.handler}; no '-= {x.handler}' anywhere" });
        File.WriteAllLines(a[2], new[] { "class\tfile\tline\tmethod\tdetail" }.Concat(Hits.Select(h => string.Join("\t", h))));
        File.WriteAllLines(a[2] + ".ranges", Ranges);
        Console.WriteLine($"methods scanned {scanned}, hits {Hits.Count}");
        foreach (var g in Hits.GroupBy(h => h[0]).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key} {g.Count()}");
        return 0;
    }

    static (string, string) Owner(SyntaxNode md)
    {
        string name = md switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            OperatorDeclarationSyntax o => "op_" + o.OperatorToken.Text,
            AccessorDeclarationSyntax ac => ac.Parent?.Parent switch
            {
                PropertyDeclarationSyntax pd => pd.Identifier.Text,
                EventDeclarationSyntax ed => ed.Identifier.Text,
                IndexerDeclarationSyntax => "Item",
                _ => null
            },
            _ => null
        };
        if (name == null) return (null, null);
        var parts = new List<string>();
        for (var n = md.Parent; n != null; n = n.Parent)
        {
            if (n is BaseTypeDeclarationSyntax td) parts.Insert(0, td.Identifier.Text);
            else if (n is BaseNamespaceDeclarationSyntax ns) parts.Insert(0, ns.Name.ToString());
        }
        if (parts.Count == 0) return (null, null);
        // file-scoped namespace is a sibling scope in newer ILSpy output
        var fsn = md.SyntaxTree.GetRoot().DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fsn != null && !parts[0].Contains('.') && !string.Join(".", parts).StartsWith(fsn.Name.ToString())) parts.Insert(0, fsn.Name.ToString());
        return (string.Join(".", parts), name);
    }

    static string HandlerName(ExpressionSyntax e)
    {
        if (e is ObjectCreationExpressionSyntax oc && oc.ArgumentList?.Arguments.Count == 1) e = oc.ArgumentList.Arguments[0].Expression;
        if (e is MemberAccessExpressionSyntax ma) return ma.Name.Identifier.Text;
        if (e is IdentifierNameSyntax id) return id.Identifier.Text;
        if (e is AnonymousFunctionExpressionSyntax) return "<lambda>";
        return e.ToString();
    }

    static bool IsLoop(SyntaxNode n) => n is ForStatementSyntax || n is ForEachStatementSyntax || n is WhileStatementSyntax || n is DoStatementSyntax;

    static StatementSyntax LoopBody(SyntaxNode n) => n switch
    {
        ForStatementSyntax f => f.Statement,
        ForEachStatementSyntax fe => fe.Statement,
        WhileStatementSyntax w => w.Statement,
        DoStatementSyntax d => d.Statement,
        _ => null
    };

    // nodes inside 'root' that belong to it (not to nested lambdas / local functions)
    static IEnumerable<SyntaxNode> Own(SyntaxNode root) =>
        root.DescendantNodes(n => n == root || !(n is AnonymousFunctionExpressionSyntax || n is LocalFunctionStatementSyntax));

    // does the loop body contain an exit of this loop (break that binds to it, return, throw, goto, yield)?
    static bool HasExit(SyntaxNode loop)
    {
        foreach (var n in Own(LoopBody(loop)))
        {
            if (n is ReturnStatementSyntax || n is ThrowStatementSyntax || n is GotoStatementSyntax || n is YieldStatementSyntax || n is ThrowExpressionSyntax) return true;
            if (n is BreakStatementSyntax br)
            {
                var owner = br.Ancestors().First(x => IsLoop(x) || x is SwitchSectionSyntax);
                if (owner == loop) return true;
            }
            if (n is InvocationExpressionSyntax inv && Yields.Contains(CalleeName(inv))) return true;
        }
        return false;
    }

    static string CalleeName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        _ => ""
    };

    static string Receiver(InvocationExpressionSyntax inv) => inv.Expression is MemberAccessExpressionSyntax ma ? Norm(ma.Expression.ToString()) : "";
    static string Norm(string s) => s.Replace("this.", "").Replace(" ", "");

    static IEnumerable<string> WrittenNames(SyntaxNode n)
    {
        foreach (var x in Own(n))
        {
            if (x is AssignmentExpressionSyntax ae) yield return Root(ae.Left);
            else if (x is PostfixUnaryExpressionSyntax pu) yield return Root(pu.Operand);
            else if (x is PrefixUnaryExpressionSyntax pr && (pr.IsKind(SyntaxKind.PreIncrementExpression) || pr.IsKind(SyntaxKind.PreDecrementExpression))) yield return Root(pr.Operand);
            else if (x is ArgumentSyntax arg && !arg.RefKindKeyword.IsKind(SyntaxKind.None)) yield return Root(arg.Expression);
            else if (x is VariableDeclaratorSyntax vd) yield return vd.Identifier.Text;
            else if (x is ForEachStatementSyntax fe) yield return fe.Identifier.Text;
            else if (x is DeclarationExpressionSyntax de && de.Designation is SingleVariableDesignationSyntax sv) yield return sv.Identifier.Text;
        }
    }
    static string Root(ExpressionSyntax e)
    {
        while (true)
        {
            switch (e)
            {
                case MemberAccessExpressionSyntax ma when ma.Expression is ThisExpressionSyntax: return ma.Name.Identifier.Text;
                case MemberAccessExpressionSyntax ma: e = ma.Expression; break;
                case ElementAccessExpressionSyntax ea: e = ea.Expression; break;
                case ParenthesizedExpressionSyntax pe: e = pe.Expression; break;
                case IdentifierNameSyntax id: return id.Identifier.Text;
                default: return e.ToString();
            }
        }
    }
    static HashSet<string> Idents(SyntaxNode n) => new(n.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text));

    static bool AlwaysExits(StatementSyntax s) => s switch
    {
        ReturnStatementSyntax or BreakStatementSyntax or ThrowStatementSyntax => true,
        BlockSyntax b => b.Statements.Count > 0 && b.Statements.Any(AlwaysExitsTop),
        IfStatementSyntax i => i.Else != null && AlwaysExits(i.Statement) && AlwaysExits(i.Else.Statement),
        _ => false
    };
    static bool AlwaysExitsTop(StatementSyntax s) => AlwaysExits(s);
    static bool HasContinue(SyntaxNode loop) => Own(LoopBody(loop)).OfType<ContinueStatementSyntax>().Any(c => c.Ancestors().First(IsLoop) == loop);

    static bool IsScanExpr(ExpressionSyntax e)
    {
        foreach (var n in e.DescendantNodesAndSelf())
        {
            if (n is InvocationExpressionSyntax inv && Scan.Contains(CalleeName(inv))) return true;
            if (n is MemberAccessExpressionSyntax ma && Scan.Contains(ma.Name.Identifier.Text) && !(ma.Parent is InvocationExpressionSyntax)) return true;
            if (n is IdentifierNameSyntax id && Scan.Contains(id.Identifier.Text) && !(id.Parent is MemberAccessExpressionSyntax) && !(id.Parent is InvocationExpressionSyntax)) return true;
        }
        return false;
    }

    static void Walk(SyntaxNode body)
    {
        foreach (var n in body.DescendantNodesAndSelf())
        {
            if (IsLoop(n)) CheckLoop(n);
            if (n is BlockSyntax blk) CheckNullAfterDeref(blk);
            if (n is AssignmentExpressionSyntax ae && ae.IsKind(SyntaxKind.AddAssignmentExpression)) CheckEventAdd(ae);
            if (n is ElementAccessExpressionSyntax el && el.ArgumentList.Arguments.Count == 1)
            {
                var arg = Norm(el.ArgumentList.Arguments[0].ToString());
                var coll = Norm(el.Expression.ToString());
                if (arg == coll + ".Count" || arg == coll + ".Length") Hit("OFFBYONE", n, $"{el} indexes one past the end");
            }
        }
    }

    static void CheckLoop(SyntaxNode loop)
    {
        var body = LoopBody(loop);
        if (body == null) return;
        // INFLOOP
        ExpressionSyntax cond = loop switch { ForStatementSyntax f0 => f0.Condition, WhileStatementSyntax w => w.Condition, DoStatementSyntax d => d.Condition, _ => null };
        if (!(loop is ForEachStatementSyntax))
        {
            bool constTrue = cond == null || cond.IsKind(SyntaxKind.TrueLiteralExpression);
            if (constTrue && !HasExit(loop)) Hit("INFLOOP", loop, "constant-true loop with no break/return/throw/yield");
            else if (!constTrue && !HasExit(loop) && !cond.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any())
            {
                var condIds = Idents(cond);
                var written = new HashSet<string>(WrittenNames(body));
                if (loop is ForStatementSyntax fs) foreach (var inc in fs.Incrementors) written.UnionWith(WrittenNames(inc).Concat(Idents(inc)));
                bool anyCall = Own(body).OfType<InvocationExpressionSyntax>().Any();
                bool memberCond = cond.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any();
                if (!condIds.Overlaps(written) && !(anyCall && memberCond) && !anyCall)
                    Hit("INFLOOP", loop, $"condition '{cond}' never changes in the body");
            }
        }
        // FIRSTEXIT: body always leaves the loop on the first pass
        if (!HasContinue(loop) && AlwaysExits(body)) Hit("FIRSTEXIT", loop, "loop body always exits on the first item");

        // NESTEDSCAN / REPEATCALL
        ExpressionSyntax src = loop is ForEachStatementSyntax fe0 ? fe0.Expression : cond;
        bool outerScan = src != null && IsScanExpr(src);
        var loopVars = new HashSet<string>(WrittenNames(body));
        if (loop is ForEachStatementSyntax fe1) loopVars.Add(fe1.Identifier.Text);
        if (loop is ForStatementSyntax f1) { if (f1.Declaration != null) foreach (var v in f1.Declaration.Variables) loopVars.Add(v.Identifier.Text); foreach (var inc in f1.Incrementors) loopVars.UnionWith(Idents(inc)); }
        var seenRepeat = new HashSet<string>();
        foreach (var inner in Own(body))
        {
            if (outerScan && IsLoop(inner) && inner.Ancestors().First(IsLoop) == loop)
            {
                ExpressionSyntax isrc = inner is ForEachStatementSyntax ife ? ife.Expression : (inner as ForStatementSyntax)?.Condition;
                if (isrc != null && IsScanExpr(isrc)) Hit("NESTEDSCAN", inner, $"scan '{Short(isrc)}' inside loop over '{Short(src)}'");
            }
            if (inner is InvocationExpressionSyntax inv && Scan.Contains(CalleeName(inv)) && inner.Ancestors().First(IsLoop) == loop)
            {
                var ids = Idents(inv);
                if (!ids.Overlaps(loopVars) && seenRepeat.Add(inv.ToString()))
                    Hit("REPEATCALL", inv, $"'{Short(inv)}' runs every iteration with loop-invariant inputs");
            }
        }
        // MODENUM: foreach over C while body mutates C and does not leave right after
        if (loop is ForEachStatementSyntax fe)
        {
            var c = Norm(fe.Expression.ToString());
            foreach (var inv in Own(body).OfType<InvocationExpressionSyntax>())
            {
                if (!Mutators.Contains(CalleeName(inv)) || Receiver(inv) != c) continue;
                if (LeavesAfter(inv, fe)) continue;
                Hit("MODENUM", inv, $"'{c}.{CalleeName(inv)}' while enumerating '{c}'");
            }
        }
        if (loop is ForStatementSyntax f)
        {
            var iv = f.Declaration?.Variables.FirstOrDefault()?.Identifier.Text
                     ?? (f.Initializers.FirstOrDefault() as AssignmentExpressionSyntax)?.Left.ToString();
            if (iv == null) return;
            var incIds = f.Incrementors.SelectMany(i => Idents(i)).ToHashSet();
            var condIds = f.Condition != null ? Idents(f.Condition) : new HashSet<string>();
            // WRONGVAR
            // ILSpy hoists a cached bound: "int i = 0; for (int n = a.Length; i < n; i++)" is fine
            if (f.Incrementors.Count > 0 && !incIds.Contains(iv) && !(condIds.Contains(iv) && condIds.Overlaps(incIds))) Hit("WRONGVAR", f, $"loop declares '{iv}' but increments '{string.Join(",", f.Incrementors)}'");
            if (f.Condition != null && !condIds.Contains(iv)) Hit("WRONGVAR", f, $"loop over '{iv}' tests '{f.Condition}'");
            if (!Idents(body).Contains(iv) && f.Condition != null)
            {
                var outer = f.Ancestors().OfType<ForStatementSyntax>().FirstOrDefault();
                var ov = outer?.Declaration?.Variables.FirstOrDefault()?.Identifier.Text;
                if (ov != null && Own(body).OfType<ElementAccessExpressionSyntax>().Any(e => e.ArgumentList.ToString() == "[" + ov + "]"))
                    Hit("WRONGVAR", f, $"inner loop '{iv}' never used; body indexes with outer '{ov}'");
            }
            // OFFBYONE
            if (f.Condition is BinaryExpressionSyntax be)
            {
                var r = Norm(be.Right.ToString());
                var start = f.Declaration?.Variables.FirstOrDefault()?.Initializer?.Value.ToString();
                if (be.IsKind(SyntaxKind.LessThanOrEqualExpression) && Norm(be.Left.ToString()) == iv && start != "1" && (r.EndsWith(".Count") || r.EndsWith(".Length")))
                    Hit("OFFBYONE", f, $"'{f.Condition}' runs one past the end");
            }
            var init = f.Declaration?.Variables.FirstOrDefault()?.Initializer?.Value.ToString();
            if (init != null && (Norm(init).EndsWith(".Count") || Norm(init).EndsWith(".Length")) && Own(body).OfType<ElementAccessExpressionSyntax>().Any(e => e.ArgumentList.ToString() == "[" + iv + "]"))
            {
                // first pass indexes at Count unless the body or condition pre-decrements
                if (!(f.Condition?.ToString().Contains("--") ?? false)) Hit("OFFBYONE", f, $"starts at '{init}' and indexes [{iv}]");
            }
            // FWDREMOVE: ascending index loop removing at the index without stepping back
            bool ascending = f.Incrementors.Any(i => i.ToString() == iv + "++" || i.ToString() == "++" + iv || i.ToString() == iv + " += 1");
            if (ascending)
            {
                foreach (var inv in Own(body).OfType<InvocationExpressionSyntax>())
                {
                    var nm = CalleeName(inv);
                    var args = inv.ArgumentList.Arguments;
                    bool atIdx = (nm == "RemoveAt" && args.Count == 1 && args[0].ToString() == iv)
                                 || (nm == "Remove" && args.Count == 1 && Norm(args[0].ToString()) == Receiver(inv) + "[" + iv + "]");
                    if (nm == "RemoveAt" && f.Condition is BinaryExpressionSyntax cb && !Norm(cb.Right.ToString()).StartsWith(Receiver(inv) + ".")) atIdx = false;
                    if (!atIdx) continue;
                    bool stepBack = Own(body).Any(x => x.ToString() == iv + "--" || x.ToString() == "--" + iv || x.ToString() == iv + " -= 1");
                    if (!stepBack && !LeavesAfter(inv, f)) Hit("FWDREMOVE", inv, $"'{inv}' in ascending loop over '{iv}' skips the next item");
                }
            }
        }
    }

    // after node n, does every path leave 'loop' before the next iteration (a later statement in an
    // enclosing block, up to the loop body, that always exits)?
    static bool LeavesAfter(SyntaxNode n, SyntaxNode loop)
    {
        var body = LoopBody(loop);
        foreach (var st in n.Ancestors().OfType<StatementSyntax>())
        {
            if (st == body) return false;
            if (st.Parent is BlockSyntax pb)
                for (int k = pb.Statements.IndexOf(st) + 1; k < pb.Statements.Count; k++)
                    if (AlwaysExits(pb.Statements[k])) return true;
        }
        return false;
    }

    static string Short(SyntaxNode n) { var s = n.ToString().Replace("\n", " ").Replace("\t", " "); return s.Length > 80 ? s.Substring(0, 80) + "..." : s; }

    // x.M(...) or x.F at statement k, then "x == null" / "x != null" at a later statement with no write to x in between
    static void CheckNullAfterDeref(BlockSyntax blk)
    {
        var deref = new Dictionary<string, SyntaxNode>();
        foreach (var st in blk.Statements)
        {
            foreach (var be in st.DescendantNodesAndSelf(n => !(n is AnonymousFunctionExpressionSyntax)).OfType<BinaryExpressionSyntax>())
            {
                if (!(be.IsKind(SyntaxKind.EqualsExpression) || be.IsKind(SyntaxKind.NotEqualsExpression))) continue;
                if (!be.Right.IsKind(SyntaxKind.NullLiteralExpression)) continue;
                if (be.Left is IdentifierNameSyntax id && deref.TryGetValue(id.Identifier.Text, out var at))
                {
                    Hit("NULLAFTERDEREF", be, $"'{id}' tested for null after use at line {at.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    deref.Remove(id.Identifier.Text);
                }
            }
            foreach (var w in WrittenNames(st)) deref.Remove(w);
            // only unconditional top-level dereferences of this statement count (not inside ?:, &&, if bodies)
            if (st is ExpressionStatementSyntax || st is LocalDeclarationStatementSyntax || st is ReturnStatementSyntax)
            {
                foreach (var ma in st.DescendantNodes(n => !(n is AnonymousFunctionExpressionSyntax || n is ConditionalExpressionSyntax || n is BinaryExpressionSyntax bx && (bx.IsKind(SyntaxKind.LogicalAndExpression) || bx.IsKind(SyntaxKind.LogicalOrExpression) || bx.IsKind(SyntaxKind.CoalesceExpression)) || n is ConditionalAccessExpressionSyntax)).OfType<MemberAccessExpressionSyntax>())
                {
                    if (ma.Expression is IdentifierNameSyntax id && char.IsLower(id.Identifier.Text[0]) && !deref.ContainsKey(id.Identifier.Text))
                        deref[id.Identifier.Text] = ma;
                }
            }
        }
    }

    static void CheckEventAdd(AssignmentExpressionSyntax ae)
    {
        var r = ae.Right;
        bool delegateLike = r is ObjectCreationExpressionSyntax oc && oc.Type.ToString().EndsWith("Handler") || r is AnonymousFunctionExpressionSyntax
                            || (r is IdentifierNameSyntax or MemberAccessExpressionSyntax) && char.IsUpper(HandlerName(r)[0]) && ae.Left.ToString().Contains("On") ;
        if (!delegateLike) return;
        var h = HandlerName(r);
        var line = ae.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        Adds.Add((File_, Meth, line, h, ae.Left.ToString()));
    }
}
