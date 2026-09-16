namespace RevitMcp.Tools
{
    internal static class AllTools
    {
        public static void RegisterAll()
        {
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
