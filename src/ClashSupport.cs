using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RevitMcp
{
    // Pure numeric helpers: these are also tested without loading Revit.
    internal sealed class ClashBounds
    {
        public readonly double[] Min;
        public readonly double[] Max;

        public ClashBounds(double[] min, double[] max)
        {
            if (min == null || max == null || min.Length != 3 || max.Length != 3)
                throw new ArgumentException("Bounds must have three coordinates.");
            Min = (double[])min.Clone(); Max = (double[])max.Clone();
            for (int i = 0; i < 3; i++)
                if (double.IsNaN(Min[i]) || double.IsInfinity(Min[i]) ||
                    double.IsNaN(Max[i]) || double.IsInfinity(Max[i]) || Min[i] > Max[i])
                    throw new ArgumentException("Bounds must be finite and ordered.");
        }

        public bool Intersects(ClashBounds other)
        {
            for (int i = 0; i < 3; i++)
                if (Min[i] > other.Max[i] || Max[i] < other.Min[i]) return false;
            return true;
        }

        public ClashBounds Union(ClashBounds other)
        {
            double[] min = new double[3], max = new double[3];
            for (int i = 0; i < 3; i++) { min[i] = Math.Min(Min[i], other.Min[i]); max[i] = Math.Max(Max[i], other.Max[i]); }
            return new ClashBounds(min, max);
        }

        public ClashBounds Expand(double distance)
        {
            if (distance < 0 || double.IsNaN(distance) || double.IsInfinity(distance))
                throw new ArgumentException("Padding must be finite and nonnegative.");
            double[] min = new double[3], max = new double[3];
            for (int i = 0; i < 3; i++) { min[i] = Min[i] - distance; max[i] = Max[i] + distance; }
            return new ClashBounds(min, max);
        }
    }

    internal static class ClashIdentity
    {
        public static string Pair(string a, string b)
        {
            // Length prefixes avoid ambiguous delimiters; sorting makes pairs symmetric.
            if (string.CompareOrdinal(a, b) > 0) { string swap = a; a = b; b = swap; }
            string canonical = a.Length.ToString(CultureInfo.InvariantCulture) + ":" + a + b.Length.ToString(CultureInfo.InvariantCulture) + ":" + b;
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        }

        public static string UniqueViewName(string prefix, string suffix, HashSet<string> existing)
        {
            StringBuilder safe = new StringBuilder();
            foreach (char c in prefix ?? "MCP Clash")
                if (!char.IsControl(c) && "\\:{}[]|;<>?`~".IndexOf(c) < 0) safe.Append(c);
            string stem = safe.ToString().Trim();
            if (stem.Length == 0) stem = "MCP Clash";
            if (stem.Length > 70) stem = stem.Substring(0, 70);
            stem += "_" + suffix;
            string candidate = stem; int n = 2;
            while (!existing.Add(candidate)) candidate = stem + "_" + (n++).ToString(CultureInfo.InvariantCulture);
            return candidate;
        }
    }
}
