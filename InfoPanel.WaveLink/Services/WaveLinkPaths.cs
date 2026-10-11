namespace InfoPanel.WaveLink.Services;

/// <summary>
/// Where the plugin keeps its own config (wavelink.json). Lives next to the
/// user profile so a plugin (re)install never wipes it.
/// </summary>
public static class WaveLinkPaths
{
    public static string DataDirectory
    {
        get
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InfoPanel",
                "WaveLink");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }
    }

    public static string ConfigFile(string name) => Path.Combine(DataDirectory, name);
}
