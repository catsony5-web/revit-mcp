using Autodesk.Revit.DB;

namespace RevitMcp
{
    // MCP 스키마는 전부 mm 를 쓰고 Revit 내부 단위는 ft 다. 변환은 여기로 모은다.
    internal static class U
    {
        public const double MmPerFt = 304.8;

        public static double ToFt(double mm) { return mm / MmPerFt; }
        public static double ToMm(double ft) { return ft * MmPerFt; }

        public static XYZ PtFt(double xMm, double yMm, double zMm)
        {
            return new XYZ(ToFt(xMm), ToFt(yMm), ToFt(zMm));
        }

        public static XYZ ToMmPt(XYZ ft)
        {
            return ft == null ? null : new XYZ(ToMm(ft.X), ToMm(ft.Y), ToMm(ft.Z));
        }
    }
}
