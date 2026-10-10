using System.Text;

namespace Jellyfin.Plugin.AudioVault.Services;

public static class PrivateFile
{
    /// <summary>Writes through a temporary file that is private from the moment it exists, then swaps it in.</summary>
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.Delete(tmp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(tmp, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
