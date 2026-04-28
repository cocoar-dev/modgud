namespace Cocoar.Auth.Api.Helper;

public class PathHelper
{

    public static string ContentPath = Directory.GetCurrentDirectory();


    public static string GetFullPath(string path, string? basePath = null)
    {
        if (String.IsNullOrWhiteSpace(basePath))
        {
            basePath = ContentPath;
        }
        var p = Path.GetFullPath(Path.Combine(basePath, path));
        return p.Replace("\\","/");
    }
}
