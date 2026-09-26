using System.Diagnostics;
using System.Text.Json;
using AutomaTable.Tool.Schema;

namespace AutomaTable.Tool.ProjectSystem;

internal sealed record ResolvedProjectSchema(string ProjectPath, SchemaManifest Manifest);

internal static class ProjectSchemaResolver
{
    public static ResolvedProjectSchema Resolve(string startDirectory, string? projectOption)
    {
        if (!string.IsNullOrWhiteSpace(projectOption))
        {
            var explicitPath = Path.GetFullPath(projectOption, startDirectory);
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException("The specified project does not exist.", explicitPath);
            if (!string.Equals(Path.GetExtension(explicitPath), ".csproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("--project must point to a .csproj file.");

            return TryBuildSchema(explicitPath, out var result, out var failure)
                ? result!
                : throw new InvalidDataException(failure);
        }

        var root = FindProjectRoot(Path.GetFullPath(startDirectory));
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(static path => !HasBuildDirectorySegment(path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (projects.Length == 0)
            throw new InvalidDataException($"No .csproj files were found under '{root}'.");

        var matches = new List<ResolvedProjectSchema>();
        var failures = new List<string>();
        foreach (var project in projects)
        {
            try
            {
                if (!ReferencesAutomaTable(project))
                    continue;
            }
            catch (Exception exception)
            {
                failures.Add(project + ": MSBuild evaluation failed (" + exception.Message + ")");
                continue;
            }

            if (TryBuildSchema(project, out var result, out var failure))
                matches.Add(result!);
            else if (!string.IsNullOrEmpty(failure))
                failures.Add(failure);
        }

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count > 1)
        {
            var lines = matches.Select((match, index) =>
                $"{index + 1}. {Path.GetRelativePath(root, match.ProjectPath)} ({match.Manifest.Tables.Count} tables)");
            throw new InvalidDataException(
                "Found multiple AutomaTable projects:" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine +
                "Specify one with --project <path-to-csproj>.");
        }

        var detail = failures.Count == 0
            ? string.Empty
            : Environment.NewLine + "Projects that failed to evaluate:" + Environment.NewLine +
              string.Join(Environment.NewLine, failures.Take(5).Select(static value => "- " + value));
        throw new InvalidDataException(
            $"No project containing generated AutomaTable schema metadata was found under '{root}'." + detail);
    }

    private static bool TryBuildSchema(
        string projectPath,
        out ResolvedProjectSchema? result,
        out string failure)
    {
        result = null;
        failure = string.Empty;
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var artifactsPath = Path.Combine(projectDirectory, "obj", "AutomaTable", "SchemaBuild");
        var assetsPath = Path.Combine(
            artifactsPath,
            "obj",
            Path.GetFileNameWithoutExtension(projectPath),
            "project.assets.json");
        Directory.CreateDirectory(artifactsPath);

        if (!File.Exists(assetsPath))
        {
            var restore = RunDotnet(
                projectDirectory,
                "restore",
                projectPath,
                "--artifacts-path",
                artifactsPath,
                "--nologo",
                "--verbosity",
                "quiet");
            if (restore.ExitCode != 0)
            {
                failure = $"{projectPath}: schema restore failed ({FirstUsefulLine(restore.StandardError, restore.StandardOutput)})";
                return false;
            }
        }

        var build = RunDotnet(
            projectDirectory,
            "build",
            projectPath,
            "--configuration",
            "Release",
            "--artifacts-path",
            artifactsPath,
            "-p:AutomaTableEmitSchema=true",
            "--no-restore",
            "--nologo",
            "--verbosity",
            "quiet");
        if (build.ExitCode != 0)
        {
            failure = $"{projectPath}: schema build failed ({FirstUsefulLine(build.StandardError, build.StandardOutput)})";
            return false;
        }

        var assemblyName = GetAssemblyName(projectPath);
        var outputDirectory = Path.Combine(artifactsPath, "bin");
        if (!Directory.Exists(outputDirectory))
        {
            failure = $"{projectPath}: schema build did not produce an artifacts bin directory.";
            return false;
        }

        foreach (var assemblyPath in Directory
                     .EnumerateFiles(outputDirectory, assemblyName + ".dll", SearchOption.AllDirectories)
                     .OrderByDescending(static path => File.GetLastWriteTimeUtc(path)))
        {
            try
            {
                var schema = SchemaMetadataReader.TryRead(assemblyPath);
                if (schema != null)
                {
                    result = new ResolvedProjectSchema(projectPath, schema);
                    return true;
                }
            }
            catch (BadImageFormatException)
            {
                // Native or otherwise non-managed output.
            }
        }

        failure = $"{projectPath}: the target assembly did not contain AutomaTable schema metadata.";
        return false;
    }

    private static ProcessResult RunDotnet(string workingDirectory, params string[] arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            if (current.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any())
                return current.FullName;
            current = current.Parent;
        }

        return startDirectory;
    }

    private static string GetAssemblyName(string projectPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)!
            }
        };
        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("-nologo");
        process.StartInfo.ArgumentList.Add("-getProperty:AssemblyName");
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Could not evaluate '{projectPath}': {FirstUsefulLine(error, output)}");

        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => value.Trim())
            .LastOrDefault(static value => value.Length > 0)
            ?? Path.GetFileNameWithoutExtension(projectPath);
    }

    private static bool ReferencesAutomaTable(string projectPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)!
            }
        };
        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("-nologo");
        process.StartInfo.ArgumentList.Add("-getItem:PackageReference");
        process.StartInfo.ArgumentList.Add("-getItem:ProjectReference");
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException(FirstUsefulLine(error, output));

        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("Items", out var items))
            return false;

        if (items.TryGetProperty("PackageReference", out var packages))
        {
            foreach (var package in packages.EnumerateArray())
            {
                if (package.TryGetProperty("Identity", out var identity) &&
                    string.Equals(identity.GetString(), "AutomaTable", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (items.TryGetProperty("ProjectReference", out var projectReferences))
        {
            foreach (var projectReference in projectReferences.EnumerateArray())
            {
                if (projectReference.TryGetProperty("FullPath", out var fullPath) &&
                    string.Equals(Path.GetFileName(fullPath.GetString()), "AutomaTable.csproj", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasBuildDirectorySegment(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static segment => segment is "bin" or "obj" or ".git");
    }

    private static string FirstUsefulLine(params string[] outputs)
    {
        foreach (var output in outputs)
        {
            var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            if (line != null)
                return line.Trim();
        }
        return "no diagnostic output";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
