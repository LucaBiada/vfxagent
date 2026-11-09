// Regression self-test for the VFX authoring tool. Runs the known-good op scripts, checks the
// log / Describe strings they must produce, and writes a markdown report. Call from
// `unity command eval`: return VfxSelfTest.Run(@"C:/.../report.md");
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

public static class VfxSelfTest
{
    const string Dir = "Assets/VfxTests/SelfTest";
    const string Root = Dir + "/";
    const string Src = "Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Loop.vfx";

    sealed class Check
    {
        public string From;    // "log" or "describe"
        public string Asset;   // describe target
        public string Want;
        public bool Not;
        public int Count = -1; // >= 0: exact number of occurrences
    }

    sealed class Case
    {
        public string Name;
        public string Script;
        public Check[] Checks;
    }

    public static string Run(string reportPath)
    {
        AssetDatabase.DeleteAsset(Dir);
        EnsureFolder(Dir);

        var body = new StringBuilder();
        int passed = 0, total = 0, casesPassed = 0;
        var cases = BuildCases();
        foreach (var c in cases)
        {
            string log;
            try { log = VfxGraphOps.Apply(c.Script); }
            catch (Exception e) { log = "EXCEPTION: " + e; }

            var describes = new Dictionary<string, string>();
            var lines = new List<string>();
            int ok = 0;
            foreach (var chk in c.Checks)
            {
                total++;
                string text;
                try { text = chk.From == "log" ? log : DescribeCached(describes, chk.Asset); }
                catch (Exception e) { text = "EXCEPTION: " + e; }

                bool pass;
                string detail = "";
                if (chk.Count >= 0)
                {
                    int n = Occurrences(text, chk.Want);
                    pass = n == chk.Count;
                    detail = $"{n} occurrence(s)";
                }
                else
                {
                    bool has = text.IndexOf(chk.Want, StringComparison.Ordinal) >= 0;
                    pass = chk.Not ? !has : has;
                }
                if (pass) { passed++; ok++; }

                string where = chk.From == "log" ? "log" : "describe(" + chk.Asset + ")";
                string verb = chk.Not ? "does not contain" : chk.Count >= 0 ? $"has {chk.Count}x" : "contains";
                lines.Add($"- [{(pass ? "x" : " ")}] {where} {verb} `{Flat(chk.Want)}`{(pass ? "" : "  (" + detail + ")")}");
            }

            bool caseOk = ok == c.Checks.Length;
            if (caseOk) casesPassed++;
            body.AppendLine($"## {c.Name} - {(caseOk ? "PASS" : "FAIL")} ({ok}/{c.Checks.Length})");
            foreach (var line in lines) body.AppendLine(line);
            if (!caseOk)
            {
                body.AppendLine().AppendLine("op log:");
                body.AppendLine("```text").AppendLine(log.TrimEnd()).AppendLine("```");
            }
            body.AppendLine();
        }

        string summary = $"selftest: {passed}/{total} checks passed";
        var report = new StringBuilder();
        report.AppendLine("# VFX self-test report").AppendLine();
        report.AppendLine(summary).AppendLine();
        report.AppendLine($"cases: {casesPassed}/{cases.Length} passed").AppendLine();
        report.Append(body);

        var folder = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(reportPath, report.ToString());
        return summary;
    }

    static Case[] BuildCases()
    {
        string bb = Root + "Blackboard.vfx", err = Root + "BlackboardErr.vfx";
        string sub = Root + "SubDouble.vfxoperator", parent = Root + "SubParent.vfx";
        string block = Root + "SubBlock.vfxblock", parent2 = Root + "SubParent2.vfx";
        string t21 = Root + "T21.vfx";
        string clipA = Root + "ClipA.vfx", clipB = Root + "ClipB.vfx";
        string clip2A = Root + "Clip2A.vfx", clip2B = Root + "Clip2B.vfx";
        string conv = Root + "Conv.vfx", convSub = Root + "Conv.vfxoperator";

        string blackboard = S(
            $"copy {Src} {bb}", "param float \"Rate\" 64", "link \"Rate\" Spawn \"Constant Spawn Rate\" Rate",
            "category add \"Spawn\"", "paramset \"Rate\" tooltip=\"Spawn rate per second\" range=0;500 category=\"Spawn\" order=0",
            "param float \"Size\" 1", "category add \"Look\"", "paramset \"Size\" category=\"Look\"",
            "category rename \"Look\" \"Visual\"", "paramrename \"Size\" \"Scale\"", "param float \"Tmp\" 2", "paramdelete \"Tmp\"",
            "paramset \"Rate\" exposed=false", "paramset \"Rate\" exposed=true value=120",
            "param int \"Mode\" 0", "paramset \"Mode\" enum=A;B;C",
            "attr add \"Heat\" float \"temperature\"", "attr rename \"Heat\" \"Warmth\"",
            "attr add \"Tag\" int", "attr order \"Tag\" 0", "attr delete \"Tag\"",
            "param float \"Gone\" 3", "category add \"Temp\"", "paramset \"Gone\" category=\"Temp\"",
            "category delete \"Temp\"", "save");

        string errors = S($"copy {Src} {err}", "param float \"A\" 1", "param float \"B\" 2", "paramrename \"A\" \"B\"");

        string subgraph = S(
            $"newsubgraph operator {sub}", "subparam in float \"X\" 10", "subparam out float \"Y\"", "addop Add a1",
            "link2 P:X Op:a1.a", "link2 P:X Op:a1.b", "link2 O:a1 P:Y", "save",
            $"copy {Src} {parent}", $"addsubgraph op {sub} S1", "setin Op:S1.X 40",
            "link2 O:S1.Y \"B:Spawn/Constant Spawn Rate/Rate\"", "save",
            $"newsubgraph block {block}", "subparam in float \"Amt\" 2", "save",
            $"copy {Src} {parent2}", $"addsubgraph block Update {block}", "save",
            $"open {sub}", $"addsubgraph op {sub} Self");

        string t21Build = S(
            $"copy {Src} {t21}", "param float \"K\" 3", "addop Multiply m1", "link2 P:K Op:m1.a",
            "link2 O:m1 \"B:Spawn/Constant Spawn Rate/Rate\"", "unlink \"B:Spawn/Constant Spawn Rate/Rate\"",
            "link2 O:m1 \"B:Spawn/Constant Spawn Rate/Rate\"", "unlink P:K Op:m1.a", "removeop #m1", "save", "");

        string clipOps = S(
            $"copy {Src} {clipA}", "param float \"K\" 3", "addop Multiply m1", "link2 P:K Op:m1.a",
            "setin Op:m1.b 5", "clipcopy P:K O:m1", "clippaste 60 0",
            "link2 O:paste1 \"B:Spawn/Constant Spawn Rate/Rate\"", "save",
            $"copy {Src} {clipB}", "clippaste", "link2 O:paste1 \"B:Spawn/Constant Spawn Rate/Rate\"", "save");

        string clipContexts = S(
            $"copy {Src} {clip2A}", "set Init \"Lifetime\" A=2 B=3", "clipcopy C:Init C:Update C:Output",
            "clippaste 0 -600", "save",
            $"copy {Src} {clip2B}", "clipcopy \"B:Update/Gravity\"", "save");

        string clipBlocks = S(
            $"open {clip2B}", "set Update \"Linear Drag\" dragCoefficient=2.5", "clipcopy \"B:Update/Linear Drag\"",
            $"open {clip2A}", "clippaste into \"Update@Update Particles\"", "save");

        string toSubgraph = S(
            $"copy {Src} {conv}", "param float \"K\" 3", "addop Multiply m1", "addop Add a1",
            "link2 P:K Op:m1.a", "setin Op:m1.b 5", "link2 O:m1 Op:a1.a", "setin Op:a1.b 10",
            "link2 O:a1 \"B:Spawn/Constant Spawn Rate/Rate\"", $"tosubgraph {convSub} O:m1 O:a1", "save");

        return new[]
        {
            C("blackboard", blackboard,
                Desc(bb, "EXPOSED 'Rate' : Single = 120"),
                Desc(bb, "category='Spawn'"),
                Desc(bb, "tooltip='Spawn rate per second'"),
                Desc(bb, "filter=Range  min=0 max=500"),
                Desc(bb, "EXPOSED 'Scale' : Single = 1"),
                Desc(bb, "category='Visual'"),
                Desc(bb, "'Tmp'", not: true),
                Desc(bb, "EXPOSED 'Mode' : Int32 = 0"),
                Desc(bb, "enum=[A, B, C]"),
                Desc(bb, "'Warmth' : Float"),
                Desc(bb, "EXPOSED 'Gone' : Single = 3"),
                Desc(bb, "exposed=True  category=''  order=0  filter=Default"),
                Desc(bb, "## Compilation errors" + Environment.NewLine + "  (none)")),
            C("errors", errors,
                Log("already exists")),
            C("subgraph", subgraph,
                Log("addsubgraph block"),
                Log("cannot reference itself"),
                Desc(parent, "in  Rate : Single = 64  <- O"),
                Desc(parent, ".Y")),
            C("unlink_removeop", t21Build,
                Desc(t21, "in  Rate : Single = 64"),
                Desc(t21, "in  Rate : Single = 64  <-", not: true)),
            C("rollback", t21Build + S($"set Spawn \"Constant Spawn Rate\" Rate=999", "param float \"Junk\" 1", "set Spawn \"NoSuchBlock\" Rate=1"),
                Log("rolled back"),
                Desc(t21, "in  Rate : Single = 64"),
                Desc(t21, "'Junk'", not: true)),
            C("flow", t21Build + S("addctx VFXPlanarPrimitiveOutput o2", "linkflow Update #o2", "unlinkflow Update #o2", "removectx #o2", "save"),
                Log("unlinked flow"),
                Log("removed context")),
            C("clipboard_operators", clipOps,
                DescCount(clipA, "b : Single = 5", 2),
                Desc(clipB, "'K' : Single = 3")),
            C("clipboard_contexts", clipContexts,
                DescCount(clip2A, "type=Init", 2),
                Desc(clip2A, "A : Single = 2")),
            C("clipboard_blocks", clipBlocks,
                Desc(clip2A, "dragCoefficient : Single = 2.5")),
            C("tosubgraph", toSubgraph,
                Log("inputs=[K] outputs=[Rate]"),
                Desc(conv, "in  Rate : Single = 64  <- O"),
                Desc(conv, ".Rate"),
                Desc(convSub, "output 'Rate' : Single"),
                Desc(convSub, "<- O")),
            C("dataspace_and_op_by_type", S($"copy {Src} {Dir}/Space.vfx", "dataspace Init World", "addop Multiply m1",
                    "setin Op:Multiply.b 3", "link2 O:Multiply \"B:Spawn/Constant Spawn Rate/Rate\"", "save"),
                Log("dataspace Init = World"),
                Log("ERROR", not: true),
                Desc($"{Dir}/Space.vfx", "in  Rate : Single = 64  <- O"),
                Desc($"{Dir}/Space.vfx", "b : Single = 3"))
        };
    }

    static string S(params string[] lines) => string.Join("\n", lines);
    static Check Log(string want, bool not = false) => new Check { From = "log", Want = want, Not = not };
    static Check Desc(string asset, string want, bool not = false) => new Check { From = "describe", Asset = asset, Want = want, Not = not };
    static Check DescCount(string asset, string want, int count) => new Check { From = "describe", Asset = asset, Want = want, Count = count };
    static Case C(string name, string script, params Check[] checks) => new Case { Name = name, Script = script, Checks = checks };

    static string DescribeCached(Dictionary<string, string> cache, string asset)
    {
        if (!cache.TryGetValue(asset, out var text))
        {
            text = VfxGraphTool.Describe(asset, true);
            cache[asset] = text;
        }
        return text;
    }

    static int Occurrences(string text, string want)
    {
        int n = 0, i = 0;
        while (i < text.Length && (i = text.IndexOf(want, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += Math.Max(1, want.Length);
        }
        return n;
    }

    static string Flat(string s) => s.Replace("\r", "").Replace("\n", "\\n");

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        if (slash > 0) EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
