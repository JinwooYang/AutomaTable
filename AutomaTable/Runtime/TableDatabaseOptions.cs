namespace AutomaTable.Runtime
{
    public enum TableLoadMode
    {
        Direct,
        Preload
    }

    public readonly struct TableDatabaseOptions
    {
        public TableDatabaseOptions(TableLoadMode loadMode)
        {
            LoadMode = loadMode;
        }

        public TableLoadMode LoadMode { get; }

        public static TableDatabaseOptions Direct =>
            new TableDatabaseOptions(TableLoadMode.Direct);

        public static TableDatabaseOptions PreloadAll =>
            new TableDatabaseOptions(TableLoadMode.Preload);
    }
}
