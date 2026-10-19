using PadShift.Core.Profile;

namespace PadShift.App.Services;

/// <summary>Result of importing a <c>.rewasd</c> into the profiles library.</summary>
public sealed record ImportResult(bool Success, string? Path, string? Name, string? Error);

/// <summary>Result of exporting the current profile to a user-chosen file.</summary>
public sealed record ExportResult(bool Success, string? Path, string? Error);

/// <summary>Result of copying the current profile into the global presets folder.</summary>
public sealed record PresetResult(bool Success, string? Path, string? Name, string? Error);

/// <summary>
/// File-system side of the library: import a foreign <c>.rewasd</c> into
/// <c>ProfilesRoot\&lt;name&gt;\Controller\</c>, export the currently loaded profile to a chosen
/// location, and save it as a preset under the sibling <c>Presets</c> folder. Every operation is
/// path-in / path-out, so it can be tested without a window or a dialog.
/// </summary>
public static class ProfileTransfer
{
    public const string ControllerFolderName = "Controller";
    public const string ProfileExtension = ".rewasd";
    public const string PresetFolderName = "Presets";

    /// <summary>Name shown for the preset group in the config library.</summary>
    public const string PresetGroupName = "预设";

    /// <summary>
    /// Copies <paramref name="sourceFile"/> into a new game folder. A duplicate game name becomes
    /// <c>Name (2)</c>, <c>Name (3)</c>, ... A file that <see cref="ProfileIo.Load"/> rejects is
    /// deleted again (together with the folder that was just created) so no half-import remains.
    /// </summary>
    public static ImportResult Import(string sourceFile, string profilesRoot, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
        {
            return new ImportResult(false, null, null, "找不到要导入的文件: " + sourceFile);
        }

        if (string.IsNullOrWhiteSpace(profilesRoot))
        {
            return new ImportResult(false, null, null, "未设置配置库目录");
        }

        string baseName = Sanitize(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourceFile) : name!);
        if (baseName.Length == 0)
        {
            baseName = "导入配置";
        }

        string? gameFolder = null;
        try
        {
            Directory.CreateDirectory(profilesRoot);
            gameFolder = ReserveFolder(profilesRoot, baseName);
            string controllerFolder = Path.Combine(gameFolder, ControllerFolderName);
            Directory.CreateDirectory(controllerFolder);

            string destination = Path.Combine(controllerFolder, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destination, overwrite: true);

            // The copy is only kept when it parses as a the v2.6 wire protocol profile; otherwise it is a half-import.
            ProfileIo.Load(destination);

            return new ImportResult(true, destination, Path.GetFileName(gameFolder), null);
        }
        catch (Exception ex)
        {
            if (gameFolder is not null)
            {
                TryDeleteFolder(gameFolder);
            }

            return new ImportResult(false, null, null, "导入失败: " + ex.Message);
        }
    }

    /// <summary>Saves the in-memory profile (including unsaved edits) to <paramref name="destinationFile"/>.</summary>
    public static ExportResult Export(GamepadProfile profile, string destinationFile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(destinationFile))
        {
            return new ExportResult(false, null, "未指定导出路径");
        }

        try
        {
            ProfileIo.Save(destinationFile, profile);
            return new ExportResult(true, destinationFile, null);
        }
        catch (Exception ex)
        {
            return new ExportResult(false, null, ex.Message);
        }
    }

    /// <summary>Copies the current profile into <c>Presets\&lt;name&gt;.rewasd</c> (deduplicated).</summary>
    public static PresetResult SaveAsPreset(GamepadProfile profile, string presetsRoot, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(presetsRoot))
        {
            return new PresetResult(false, null, null, "未找到预设目录（Profiles 同级的 Presets）");
        }

        string baseName = Sanitize(string.IsNullOrWhiteSpace(name) ? profile.Config?.AppName ?? "" : name!);
        if (baseName.Length == 0)
        {
            baseName = "预设";
        }

        try
        {
            Directory.CreateDirectory(presetsRoot);
            string destination = ReserveFile(presetsRoot, baseName);
            ProfileIo.Save(destination, profile);
            return new PresetResult(true, destination, Path.GetFileNameWithoutExtension(destination), null);
        }
        catch (Exception ex)
        {
            return new PresetResult(false, null, null, "另存为预设失败: " + ex.Message);
        }
    }

    /// <summary>Sibling <c>Presets</c> folder of a profiles root (the the v2.6 wire protocol layout).</summary>
    public static string DerivePresetsRoot(string? profilesRoot)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot))
        {
            return "";
        }

        try
        {
            string full = Path.GetFullPath(profilesRoot);
            DirectoryInfo? parent = Directory.GetParent(full);
            return parent is null ? "" : Path.Combine(parent.FullName, PresetFolderName);
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string ReserveFolder(string root, string baseName)
    {
        for (int attempt = 1; ; attempt++)
        {
            string candidate = attempt == 1 ? baseName : $"{baseName} ({attempt})";
            string full = Path.Combine(root, candidate);
            if (!Directory.Exists(full) && !File.Exists(full))
            {
                Directory.CreateDirectory(full);
                return full;
            }
        }
    }

    private static string ReserveFile(string root, string baseName)
    {
        for (int attempt = 1; ; attempt++)
        {
            string candidate = attempt == 1 ? baseName + ProfileExtension : $"{baseName} ({attempt}){ProfileExtension}";
            string full = Path.Combine(root, candidate);
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                return full;
            }
        }
    }

    private static string Sanitize(string value)
    {
        string text = (value ?? "").Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '_');
        }

        return text.Trim(' ', '.');
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort: a locked half-import is still reported as a failed import.
        }
    }
}
