using System;
using System.Collections.Generic;
using RevitMcp;

static class ClashSupportTests
{
    static int assertions;
    static void Assert(bool condition, string name)
    {
        assertions++; if (!condition) throw new Exception("FAIL: " + name);
    }
    static ClashBounds Box(double x, double y, double z, double X, double Y, double Z)
    { return new ClashBounds(new double[] { x, y, z }, new double[] { X, Y, Z }); }
    static void Throws(Action action, string name)
    {
        bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; }
        Assert(thrown, name);
    }
    static int Main()
    {
        ClashBounds a = Box(-2, -3, 1, 4, 5, 6), b = Box(3, -4, 2, 7, 2, 5);
        Assert(a.Intersects(b) && b.Intersects(a), "symmetric overlap");
        Assert(!a.Intersects(Box(5, -3, 1, 6, 5, 6)), "separated on X");
        Assert(!a.Intersects(Box(-2, 6, 1, 4, 7, 6)), "separated on Y");
        Assert(!a.Intersects(Box(-2, -3, 7, 4, 5, 8)), "separated on Z");
        Assert(a.Intersects(Box(4, -3, 1, 5, 5, 6)), "touching face remains a broad-phase candidate");
        Assert(a.Intersects(Box(-1, -2, 2, 3, 4, 5)), "containment");
        ClashBounds union = a.Union(b);
        Assert(union.Min[0] == -2 && union.Min[1] == -4 && union.Min[2] == 1 && union.Max[0] == 7 && union.Max[1] == 5 && union.Max[2] == 6, "union corners");
        ClashBounds expanded = a.Expand(0.5);
        Assert(expanded.Min[0] == -2.5 && expanded.Max[2] == 6.5, "view padding");
        Assert(a.Min[0] == -2 && a.Max[2] == 6, "padding and union preserve input");
        double[] min = { 0, 0, 0 }, max = { 1, 1, 1 }; ClashBounds copied = new ClashBounds(min, max); min[0] = -100;
        Assert(copied.Min[0] == 0, "bounds defensively copy mutable input");
        Throws(delegate { Box(1, 0, 0, 0, 1, 1); }, "inverted bounds rejected");
        Throws(delegate { Box(double.NaN, 0, 0, 1, 1, 1); }, "NaN rejected");
        Throws(delegate { Box(0, 0, 0, double.PositiveInfinity, 1, 1); }, "infinity rejected");
        Throws(delegate { a.Expand(-1); }, "negative padding rejected");
        Throws(delegate { a.Expand(double.NaN); }, "NaN padding rejected");
        string host = "host//model/wall", linked = "host/link-instance-a/model/wall", linkedAgain = "host/link-instance-b/model/wall";
        Assert(ClashIdentity.Pair(host, linked) == ClashIdentity.Pair(linked, host), "pair identity independent of scan direction");
        Assert(ClashIdentity.Pair(host, linked) != ClashIdentity.Pair(host, linkedAgain), "same source model in two link placements stays distinct");
        Assert(ClashIdentity.Pair("ab", "c") != ClashIdentity.Pair("a", "bc"), "length prefixes disambiguate pairs");
        Assert(ClashIdentity.Pair(host, linked).Length == 64, "full SHA256 issue id");
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.Add("MCP Clash_issue"); names.Add("MCP Clash_issue_2");
        Assert(ClashIdentity.UniqueViewName("MCP Clash", "issue", names) == "MCP Clash_issue_3", "existing user view names never overwritten");
        Assert(ClashIdentity.UniqueViewName("mcp clash", "issue", names) == "mcp clash_issue_4", "name collisions compared case-insensitively");
        Assert(ClashIdentity.UniqueViewName("[]{}\\:;|<>?`~", "x", names) == "MCP Clash_x", "invalid prefix has safe fallback");
        Assert(ClashIdentity.UniqueViewName(new string('x', 100), "id", names).Length == 73, "prefix length bounded");
        Console.WriteLine("PASS: " + assertions + " offline clash geometry/identity/view-name assertions.");
        return 0;
    }
}
