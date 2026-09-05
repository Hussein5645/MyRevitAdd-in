using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyRevitPlugin;

internal sealed record ProjectGalleryInfo(string ProjectId, string DisplayName, string DirectoryPath);

internal static class RenderGalleryStorage
{
    private const string MetadataFileName = "project.json";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "MyRevitPlugin",
        "SavedRenders");

    public static ProjectGalleryInfo ForDocument(Document document)
    {
        Directory.CreateDirectory(RootDirectory);
        string displayName = string.IsNullOrWhiteSpace(document.Title) ? "Untitled Revit Project" : document.Title;
        string? projectId = document.ProjectInformation?.UniqueId;
        if (string.IsNullOrWhiteSpace(projectId))
            projectId = document.PathName is { Length: > 0 } ? document.PathName : displayName;

        string folderName = $"{SafeSegment(displayName, 58)}__{SafeSegment(projectId, 72)}";
        string directory = Path.Combine(RootDirectory, folderName);
        Directory.CreateDirectory(directory);
        WriteMetadata(directory, projectId, displayName);
        return new ProjectGalleryInfo(projectId, displayName, directory);
    }

    public static IReadOnlyList<ProjectGalleryInfo> DiscoverProjects()
    {
        Directory.CreateDirectory(RootDirectory);
        var projects = new List<ProjectGalleryInfo>();
        foreach (string directory in Directory.EnumerateDirectories(RootDirectory))
        {
            string metadataPath = Path.Combine(directory, MetadataFileName);
            try
            {
                if (File.Exists(metadataPath))
                {
                    ProjectMetadata? metadata = JsonSerializer.Deserialize<ProjectMetadata>(File.ReadAllText(metadataPath));
                    if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.ProjectId))
                    {
                        projects.Add(new ProjectGalleryInfo(metadata.ProjectId, metadata.DisplayName ?? Path.GetFileName(directory), directory));
                        continue;
                    }
                }
            }
            catch (Exception exception)
            {
                RenderLog.Error("gallery-metadata", exception);
            }

            projects.Add(new ProjectGalleryInfo(Path.GetFileName(directory), Path.GetFileName(directory), directory));
        }

        return projects.OrderBy(project => project.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string SaveRender(string sourcePath, ProjectGalleryInfo project)
    {
        Directory.CreateDirectory(project.DirectoryPath);
        WriteMetadata(project.DirectoryPath, project.ProjectId, project.DisplayName);
        string savedPath = Path.Combine(project.DirectoryPath, $"AI-Render-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.Copy(sourcePath, savedPath, false);
        return savedPath;
    }

    public static bool IsDirectlyInRoot(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void WriteMetadata(string directory, string projectId, string displayName)
    {
        string metadataPath = Path.Combine(directory, MetadataFileName);
        var metadata = new ProjectMetadata { ProjectId = projectId, DisplayName = displayName };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SafeSegment(string value, int maximumLength)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) || character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar ? '_' : character).ToArray());
        safe = safe.Trim().TrimEnd('.');
        if (safe.Length > maximumLength)
            safe = safe[..maximumLength];
        return string.IsNullOrWhiteSpace(safe) ? "Project" : safe;
    }

    private sealed class ProjectMetadata
    {
        public string ProjectId { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
    }
}
