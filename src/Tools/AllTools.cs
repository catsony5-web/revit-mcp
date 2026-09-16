namespace RevitMcp.Tools
{
    internal static class AllTools
    {
        public static void RegisterAll()
        {
            SessionTools.Register();
            AnalysisTools.Register();
            ClashTools.Register();
            CadTools.Register();
            ViewTools.Register();
            QueryTools.Register();
            MiscTools.Register();
            CreateTools.Register();
            ModifyTools.Register();
            AnnotationTools.Register();
            RoomTools.Register();
            DataTools.Register();
        }
    }
}
