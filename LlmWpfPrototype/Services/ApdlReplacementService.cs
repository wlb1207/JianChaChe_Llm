using System.IO;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class ApdlReplacementService
{
    private const string VariableAssignmentMode = "VariableAssignment";
    private const string CommandArgumentMode = "CommandArgument";

    public IReadOnlyList<ApdlGenerationLogEntry> GenerateModifiedFiles(IEnumerable<ParameterItem> parameters)
    {
        var logs = new List<ApdlGenerationLogEntry>();
        var workingFiles = new Dictionary<string, ApdlWorkingFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters.Where(item => string.Equals(item.MappingStatus, "OK", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var replacement in parameter.ApdlReplacements)
            {
                var outputPath = ResolvePath(replacement.OutputFile);
                if (!TryGetOrCreateWorkingFile(replacement, outputPath, workingFiles, logs, parameter.Name, out var workingFile))
                {
                    continue;
                }

                switch (replacement.Mode)
                {
                    case VariableAssignmentMode:
                        ApplyVariableAssignment(workingFile!, parameter, replacement, outputPath, logs);
                        break;
                    case CommandArgumentMode:
                        ApplyCommandArgument(workingFile!, parameter, replacement, outputPath, logs);
                        break;
                    default:
                        logs.Add(new ApdlGenerationLogEntry
                        {
                            ParameterName = parameter.Name,
                            ReplacementMode = replacement.Mode,
                            OutputFilePath = outputPath,
                            IsWarning = true,
                            Message = $"Warning: unsupported APDL replacement mode '{replacement.Mode}'."
                        });
                        break;
                }
            }
        }

        foreach (var workingFile in workingFiles.Values)
        {
            var directoryPath = Path.GetDirectoryName(workingFile.OutputPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            File.WriteAllText(workingFile.OutputPath, workingFile.Content);
        }

        return logs;
    }

    private static bool TryGetOrCreateWorkingFile(
        ApdlReplacement replacement,
        string outputPath,
        IDictionary<string, ApdlWorkingFile> workingFiles,
        ICollection<ApdlGenerationLogEntry> logs,
        string parameterName,
        out ApdlWorkingFile? workingFile)
    {
        if (workingFiles.TryGetValue(outputPath, out workingFile))
        {
            return true;
        }

        var sourcePath = ResolvePath(replacement.SourceFile);
        if (!File.Exists(sourcePath))
        {
            logs.Add(new ApdlGenerationLogEntry
            {
                ParameterName = parameterName,
                ReplacementMode = replacement.Mode,
                OutputFilePath = outputPath,
                IsWarning = true,
                Message = $"Warning: source APDL file not found: {sourcePath}"
            });
            return false;
        }

        workingFile = new ApdlWorkingFile
        {
            OutputPath = outputPath,
            SourcePath = sourcePath,
            Content = File.ReadAllText(sourcePath)
        };
        workingFiles[outputPath] = workingFile;
        return true;
    }

    private static void ApplyVariableAssignment(
        ApdlWorkingFile workingFile,
        ParameterItem parameter,
        ApdlReplacement replacement,
        string outputPath,
        ICollection<ApdlGenerationLogEntry> logs)
    {
        if (string.IsNullOrWhiteSpace(replacement.VariableName))
        {
            logs.Add(BuildWarning(parameter.Name, replacement.Mode, outputPath, "Warning: variable_name is empty."));
            return;
        }

        var variableName = Regex.Escape(replacement.VariableName);
        var patterns = new[]
        {
            $@"(?im)^(?<prefix>\s*{variableName}\s*=\s*)(?<value>[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?)",
            $@"(?im)^(?<prefix>\s*\*SET\s*,\s*{variableName}\s*,\s*)(?<value>[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?)"
        };

        foreach (var pattern in patterns)
        {
            var regex = new Regex(pattern);
            var match = regex.Match(workingFile.Content);
            if (!match.Success)
            {
                continue;
            }

            var originalValue = match.Groups["value"].Value;
            workingFile.Content = regex.Replace(
                workingFile.Content,
                evaluator => $"{evaluator.Groups["prefix"].Value}{parameter.Value}",
                1);

            logs.Add(new ApdlGenerationLogEntry
            {
                ParameterName = parameter.Name,
                ReplacementMode = replacement.Mode,
                OriginalValue = originalValue,
                NewValue = parameter.Value,
                OutputFilePath = outputPath,
                Message = $"参数名={parameter.Name}；替换模式={replacement.Mode}；原始值={originalValue}；新值={parameter.Value}；输出文件路径={outputPath}"
            });
            return;
        }

        logs.Add(BuildWarning(parameter.Name, replacement.Mode, outputPath, $"Warning: variable assignment not found for {replacement.VariableName}."));
    }

    private static void ApplyCommandArgument(
        ApdlWorkingFile workingFile,
        ParameterItem parameter,
        ApdlReplacement replacement,
        string outputPath,
        ICollection<ApdlGenerationLogEntry> logs)
    {
        if (string.IsNullOrWhiteSpace(replacement.Command) || !replacement.ArgumentIndex.HasValue || !replacement.Occurrence.HasValue)
        {
            logs.Add(BuildWarning(parameter.Name, replacement.Mode, outputPath, "Warning: command, occurrence, or argument_index is missing."));
            return;
        }

        var lines = Regex.Split(workingFile.Content, "\r?\n");
        var occurrence = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedLine = line.TrimStart();
            if (!trimmedLine.StartsWith(replacement.Command, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var commandLength = replacement.Command.Length;
            if (trimmedLine.Length > commandLength && trimmedLine[commandLength] != ',')
            {
                continue;
            }

            occurrence++;
            if (occurrence != replacement.Occurrence.Value)
            {
                continue;
            }

            var segments = line.Split(',');
            var segmentIndex = replacement.ArgumentIndex.Value;
            if (segments.Length <= segmentIndex)
            {
                logs.Add(BuildWarning(parameter.Name, replacement.Mode, outputPath, $"Warning: argument_index {replacement.ArgumentIndex.Value} not found for command {replacement.Command}."));
                return;
            }

            var originalSegment = segments[segmentIndex];
            var originalValue = originalSegment.Trim();
            var leadingWhitespace = Regex.Match(originalSegment, @"^\s*").Value;
            var trailingWhitespace = Regex.Match(originalSegment, @"\s*$").Value;
            segments[segmentIndex] = $"{leadingWhitespace}{parameter.Value}{trailingWhitespace}";
            lines[i] = string.Join(",", segments);
            workingFile.Content = string.Join(Environment.NewLine, lines);

            logs.Add(new ApdlGenerationLogEntry
            {
                ParameterName = parameter.Name,
                ReplacementMode = replacement.Mode,
                OriginalValue = originalValue,
                NewValue = parameter.Value,
                OutputFilePath = outputPath,
                Message = $"参数名={parameter.Name}；替换模式={replacement.Mode}；原始值={originalValue}；新值={parameter.Value}；输出文件路径={outputPath}"
            });
            return;
        }

        logs.Add(BuildWarning(parameter.Name, replacement.Mode, outputPath, $"Warning: command {replacement.Command} occurrence {replacement.Occurrence.Value} not found."));
    }

    private static ApdlGenerationLogEntry BuildWarning(string parameterName, string mode, string outputPath, string message)
    {
        return new ApdlGenerationLogEntry
        {
            ParameterName = parameterName,
            ReplacementMode = mode,
            OutputFilePath = outputPath,
            IsWarning = true,
            Message = message
        };
    }

    private static string ResolvePath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, normalized));
    }

    private sealed class ApdlWorkingFile
    {
        public string SourcePath { get; set; } = string.Empty;

        public string OutputPath { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }
}
