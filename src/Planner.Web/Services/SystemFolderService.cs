using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Planner.Web.Services;

public interface ISystemFolderService { void Open(string path); }
public sealed class SystemFolderService : ISystemFolderService
{
    public void Open(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}

public static partial class DiagnosticSanitizer
{
    [GeneratedRegex(@"[A-Z]:\\[^\r\n]+", RegexOptions.IgnoreCase)] private static partial Regex PathPattern();
    [GeneratedRegex(@"[\w.+-]+@[\w.-]+", RegexOptions.IgnoreCase)] private static partial Regex EmailPattern();
    [GeneratedRegex(@"(?i)(token|secret|authorization)\s*[:=]\s*\S+")] private static partial Regex SecretPattern();
    public static string Sanitize(string value) => SecretPattern().Replace(EmailPattern().Replace(PathPattern().Replace(value ?? "", "[path]"), "[email]"), "$1=[redacted]");
}
