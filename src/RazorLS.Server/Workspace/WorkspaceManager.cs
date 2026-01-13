using Microsoft.Extensions.Logging;

namespace RazorLS.Server.Workspace;

/// <summary>
/// Manages workspace discovery - finding solutions and projects.
/// </summary>
public class WorkspaceManager
{
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(ILogger<WorkspaceManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds a solution file in the given directory or its parents.
    /// </summary>
    public string? FindSolution(string rootPath)
    {
        var directory = new DirectoryInfo(rootPath);

        while (directory != null)
        {
            var solutions = directory.GetFiles("*.sln");
            if (solutions.Length > 0)
            {
                // If multiple solutions, prefer ones that match the directory name
                var preferred = solutions.FirstOrDefault(s =>
                    Path.GetFileNameWithoutExtension(s.Name)
                        .Equals(directory.Name, StringComparison.OrdinalIgnoreCase));

                var solution = (preferred ?? solutions[0]).FullName;
                _logger.LogDebug("Found solution: {Solution}", solution);
                return solution;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Finds all project files in the given directory and subdirectories.
    /// </summary>
    public string[] FindProjects(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        var projects = new List<string>();

        try
        {
            // Look for .csproj, .fsproj, .vbproj files
            var projectExtensions = new[] { "*.csproj", "*.fsproj", "*.vbproj" };

            foreach (var pattern in projectExtensions)
            {
                var files = Directory.GetFiles(rootPath, pattern, SearchOption.AllDirectories);
                projects.AddRange(files);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching for projects in {Path}", rootPath);
        }

        _logger.LogDebug("Found {Count} projects in {Path}", projects.Count, rootPath);
        return projects.ToArray();
    }

    /// <summary>
    /// Checks if the path contains a .NET project or solution.
    /// </summary>
    public bool IsDotNetWorkspace(string rootPath)
    {
        return FindSolution(rootPath) != null || FindProjects(rootPath).Length > 0;
    }
}
