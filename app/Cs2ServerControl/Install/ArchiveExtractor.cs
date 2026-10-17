using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Cs2ServerControl.Install;

/// <summary>
/// Extracts zip, rar and 7z archives. Zip files use .NET itself, everything
/// else SharpCompress, with an installed 7-Zip as the last fallback. The
/// tar.exe built into Windows fails on most RAR files, so it is not used.
/// </summary>
internal sealed class ArchiveExtractor {
    private readonly string _workDir;

    public ArchiveExtractor(string workDir) {
        _workDir = workDir;
    }

    /// <summary>Extracts an archive into a fresh folder below tools\work and returns that folder.</summary>
    public string Expand(string archivePath) {
        string destination = Path.Combine(_workDir, Path.GetFileNameWithoutExtension(archivePath));
        ExtractInto(archivePath, destination);
        return destination;
    }

    /// <summary>Extracts into <paramref name="destination"/>, which is emptied first.</summary>
    public static void ExtractInto(string archivePath, string destination) {
        bool isZip = Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        if (isZip && TryExtract(destination, () => ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true))) {
            return;
        }
        if (TryExtract(destination, () => ExtractWithSharpCompress(archivePath, destination))) {
            return;
        }

        string? sevenZipExe = FindSevenZip();
        if (sevenZipExe != null && TryExtract(destination, () => ExtractWithSevenZip(sevenZipExe, archivePath, destination))) {
            return;
        }

        throw new InstallException($"Could not extract {Path.GetFileName(archivePath)}. Install 7-Zip (7-zip.org) and run the installer again.");
    }

    /// <summary>Every attempt starts on an empty folder, so a half extracted try leaves nothing behind.</summary>
    private static bool TryExtract(string destination, Action extract) {
        if (Directory.Exists(destination)) {
            Directory.Delete(destination, recursive: true);
        }
        Directory.CreateDirectory(destination);
        try {
            extract();
            return true;
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            // Broken or unsupported for this reader; the next one gets a turn.
            return false;
        }
    }

    private static void ExtractWithSharpCompress(string archivePath, string destination) {
        using IArchive archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
        archive.WriteToDirectory(destination, new ExtractionOptions {
            ExtractFullPath = true,
            Overwrite = true,
        }, null);
    }

    private static void ExtractWithSevenZip(string sevenZipExe, string archivePath, string destination) {
        int exitCode = HiddenProcess.RunAsync(sevenZipExe, $"x -y \"-o{destination}\" \"{archivePath}\"", destination, _ => { }, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (exitCode != 0) {
            throw new Win32Exception($"7-Zip ended with exit code {exitCode}.");
        }
    }

    private static string? FindSevenZip() {
        string?[] candidates = [
            CombineOrNull(Environment.GetEnvironmentVariable("ProgramFiles"), @"7-Zip\7z.exe"),
            CombineOrNull(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), @"7-Zip\7z.exe"),
        ];
        return candidates.FirstOrDefault(candidate => candidate != null && File.Exists(candidate));
    }

    private static string? CombineOrNull(string? folder, string relativePath) {
        if (string.IsNullOrEmpty(folder)) {
            return null;
        }
        return Path.Combine(folder, relativePath);
    }
}
