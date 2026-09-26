using AutomaTable.Tool;
using AutomaTable.Tool.ProjectSystem;
using AutomaTable.Tool.Validation;

return CommandLine.Run(args);

internal static class CommandLine
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var command = args[0];
            var options = ParseOptions(args.Skip(1).ToArray());
            var workingDirectory = Directory.GetCurrentDirectory();
            var resolved = ProjectSchemaResolver.Resolve(workingDirectory, options.Project);
            var projectDirectory = Path.GetDirectoryName(resolved.ProjectPath)!;
            var databasePath = ResolvePath(options.Output, projectDirectory, Path.Combine("Generated", "table.db"));

            return command switch
            {
                "build" => Build(resolved, projectDirectory, databasePath, options),
                "validate" => Validate(resolved, projectDirectory, databasePath, options),
                _ => throw new ArgumentException($"Unknown command '{command}'. Expected 'build' or 'validate'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("AutomaTable: " + exception.Message);
            return 2;
        }
    }

    private static int Build(
        ResolvedProjectSchema resolved,
        string projectDirectory,
        string databasePath,
        Options options)
    {
        var inputPath = ResolvePath(options.Input, projectDirectory, "GameData");
        Console.WriteLine($"Building AutomaTable data for '{resolved.ProjectPath}'...");
        var result = TableDatabaseBuilder.Build(resolved.Manifest, inputPath, databasePath);
        Console.WriteLine($"Built {result.TotalRowCount} rows from {result.WorkbookCount} workbook(s) into '{result.DatabasePath}'.");
        foreach (var table in result.Tables)
            Console.WriteLine($"  {table.TableName}: {table.RowCount} row(s)");

        if (!options.ValidateAfterBuild)
            return 0;

        var resourcesPath = ResolvePath(options.Resources, projectDirectory, "Resources");
        return PrintValidation(TableDataValidator.Validate(resolved.Manifest, databasePath, resourcesPath));
    }

    private static int Validate(
        ResolvedProjectSchema resolved,
        string projectDirectory,
        string databasePath,
        Options options)
    {
        var resourcesPath = ResolvePath(options.Resources, projectDirectory, "Resources");
        Console.WriteLine("Validating AutomaTable data...");
        return PrintValidation(TableDataValidator.Validate(resolved.Manifest, databasePath, resourcesPath));
    }

    private static int PrintValidation(ValidationResult result)
    {
        foreach (var check in result.Checks)
        {
            if (check.Failure == null)
            {
                Console.WriteLine($"[OK] {check.Subject}");
                Console.WriteLine($"     {check.Rule}");
            }
            else
            {
                Console.Error.WriteLine($"[FAIL] {check.Subject}");
                Console.Error.WriteLine($"       {check.Rule}: {check.Failure}");
            }
        }

        Console.WriteLine(result.Succeeded ? "Validation succeeded." : "Validation failed.");
        return result.Succeeded ? 0 : 3;
    }

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--project":
                    options.Project = ReadValue(args, ref index, argument);
                    break;
                case "--input":
                    options.Input = ReadValue(args, ref index, argument);
                    break;
                case "--output":
                    options.Output = ReadValue(args, ref index, argument);
                    break;
                case "--resources":
                    options.Resources = ReadValue(args, ref index, argument);
                    break;
                case "--validate":
                    options.ValidateAfterBuild = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{argument}'.");
            }
        }
        return options;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"Option '{option}' requires a value.");
        return args[index];
    }

    private static string ResolvePath(string? configured, string projectDirectory, string defaultRelativePath)
    {
        return Path.GetFullPath(configured ?? defaultRelativePath, configured == null ? projectDirectory : Directory.GetCurrentDirectory());
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AutomaTable.Tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet automatable build [--project <path>] [--input <path>] [--output <path>] [--validate] [--resources <path>]");
        Console.WriteLine("  dotnet automatable validate [--project <path>] [--output <path>] [--resources <path>]");
        Console.WriteLine();
        Console.WriteLine("Defaults:");
        Console.WriteLine("  input      <project>/GameData");
        Console.WriteLine("  output     <project>/Generated/table.db");
        Console.WriteLine("  resources  <project>/Resources");
    }

    private sealed class Options
    {
        public string? Project { get; set; }
        public string? Input { get; set; }
        public string? Output { get; set; }
        public string? Resources { get; set; }
        public bool ValidateAfterBuild { get; set; }
    }
}
