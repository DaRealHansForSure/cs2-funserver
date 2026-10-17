using System.IO;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Install;

/// <summary>
/// Copies the map files from an extracted archive into cstrike.
/// Archives come in two shapes: with a "maps" folder (often inside cstrike\),
/// or with the .bsp and .wad lying loose. Both are handled. Existing files
/// outside the maps folder are never overwritten, so a map can not replace
/// stock models or sounds.
/// </summary>
internal sealed class MapContentCopier {
    private readonly string _cstrikeDir;

    public MapContentCopier(string cstrikeDir) {
        _cstrikeDir = cstrikeDir;
    }

    /// <summary>Copies the content and returns the names of the maps found, lower case.</summary>
    public IReadOnlyList<string> Install(string sourceDir) {
        List<string> bspFiles = [.. FileTree.FilesWithExtension(sourceDir, ".bsp", recursive: true)];
        if (bspFiles.Count == 0) {
            throw new InstallException("no .bsp file in the archive");
        }

        // Map packs hold several maps in one maps folder, copy that tree only once.
        var copiedContentRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool copiedLooseResources = false;

        foreach (string bspFile in bspFiles) {
            string bspFolder = Path.GetDirectoryName(bspFile)!;
            bool liesInMapsFolder = Path.GetFileName(bspFolder).Equals("maps", StringComparison.OrdinalIgnoreCase);
            if (liesInMapsFolder) {
                string contentRoot = Path.GetDirectoryName(bspFolder)!;
                if (copiedContentRoots.Add(contentRoot)) {
                    CopyContentTree(contentRoot);
                }
                continue;
            }

            CopyLooseCompanions(bspFile);
            // The shared files of a loose archive are the same for every map in it.
            if (!copiedLooseResources) {
                CopyLooseResources(sourceDir);
                copiedLooseResources = true;
            }
        }

        foreach (string bspFile in bspFiles) {
            CopyReferencedFiles(sourceDir, bspFile);
        }

        return [.. bspFiles.Select(file => Path.GetFileNameWithoutExtension(file).ToLowerInvariant())];
    }

    /// <summary>
    /// Some archives put a sprite or model loose next to the .bsp instead of
    /// into sprites\ or models\ (surf_water-run). The map names the path it
    /// expects, so a file of that name anywhere in the archive goes there.
    /// </summary>
    private void CopyReferencedFiles(string sourceDir, string bspFile) {
        string installedBsp = Path.Combine(_cstrikeDir, "maps", Path.GetFileName(bspFile));
        foreach (string missingPath in MapResources.FindMissing(installedBsp, _cstrikeDir)) {
            string fileName = Path.GetFileName(missingPath);
            string? found = FileTree.AllFiles(sourceDir)
                .FirstOrDefault(file => Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (found != null) {
                CopyMapFile(found, missingPath.Replace('/', Path.DirectorySeparatorChar));
            }
        }
    }

    private void CopyContentTree(string contentRoot) {
        foreach (string folderName in InstallComponents.MapContentFolders) {
            string folder = Path.Combine(contentRoot, folderName);
            foreach (string file in FileTree.AllFiles(folder)) {
                CopyMapFile(file, Path.GetRelativePath(contentRoot, file));
            }
        }

        // Map textures (.wad) usually sit next to the maps folder.
        foreach (string wadFile in FileTree.FilesWithExtension(contentRoot, ".wad", recursive: false)) {
            CopyMapFile(wadFile, Path.GetFileName(wadFile));
        }
    }

    private void CopyLooseCompanions(string bspFile) {
        string mapName = Path.GetFileNameWithoutExtension(bspFile);
        string bspFolder = Path.GetDirectoryName(bspFile)!;
        foreach (string companion in Directory.EnumerateFiles(bspFolder)) {
            bool belongsToMap = Path.GetFileName(companion).StartsWith(mapName + ".", StringComparison.OrdinalIgnoreCase);
            if (belongsToMap) {
                CopyMapFile(companion, Path.Combine("maps", Path.GetFileName(companion)));
            }
        }
    }

    private void CopyLooseResources(string sourceDir) {
        foreach (string wadFile in FileTree.FilesWithExtension(sourceDir, ".wad", recursive: true)) {
            CopyMapFile(wadFile, Path.GetFileName(wadFile));
        }

        // Resource folders can sit anywhere in a loose archive, keep their layout.
        foreach (string folderName in InstallComponents.LooseResourceFolders) {
            foreach (string folder in FileTree.FoldersNamed(sourceDir, folderName)) {
                string parentFolder = Path.GetDirectoryName(folder)!;
                foreach (string file in FileTree.AllFiles(folder)) {
                    CopyMapFile(file, Path.GetRelativePath(parentFolder, file));
                }
            }
        }
    }

    private void CopyMapFile(string sourceFile, string relativeTarget) {
        if (!InstallComponents.MapContentExtensions.Contains(Path.GetExtension(sourceFile))) {
            return;
        }

        string targetPath = Path.Combine(_cstrikeDir, relativeTarget);
        bool isInMapsFolder = relativeTarget.StartsWith("maps" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(targetPath) && !isInMapsFolder) {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourceFile, targetPath, overwrite: true);
    }
}
