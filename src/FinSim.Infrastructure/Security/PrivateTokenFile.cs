namespace FinSim.Infrastructure.Security;

/// <summary>Reads a rotating bearer token without ever caching or logging it.</summary>
public static class PrivateTokenFile
{
    public static string Read(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.Directory) != 0 ||
            info.Length is <= 0 or > 16_384)
        {
            throw new InvalidOperationException("Token file is missing or invalid.");
        }

        if (OperatingSystem.IsLinux() &&
            (File.GetUnixFileMode(fullPath) &
             (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
              UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException("Token file permissions are too broad.");
        }

        var token = File.ReadAllText(fullPath).Trim();
        if (token.Length is <= 0 or > 16_384 || token.Contains('\n') || token.Contains('\r'))
        {
            throw new InvalidOperationException("Token file content is invalid.");
        }

        return token;
    }
}
