using Tosh.Tome.Workspace;

namespace Tosh.Tests;

public class TomeWorkspaceTests
{
    [Fact]
    public void Load_resolves_relative_folders_from_workspace_file_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tome-workspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspaceDir = Path.Combine(root, "workspace");
            var sourceDir = Path.Combine(workspaceDir, "src");
            Directory.CreateDirectory(sourceDir);

            var manifest = Path.Combine(workspaceDir, "project.tome");
            File.WriteAllText(manifest, """
                workspace "project" {
                    folder "src" as "source"
                }
                """);

            var ws = WorkspaceFile.Load(manifest);

            Assert.Equal(Path.GetFullPath(manifest), ws.SourcePath);
            var folder = Assert.Single(ws.Folders);
            Assert.Equal(sourceDir, folder.Path);
            Assert.Equal("source", folder.Alias);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
