using System.Text.RegularExpressions;
namespace EgorVpn.Services;

internal static class StartupDiagnostics
{
    private static string Safe(string text)=>DisplayFormat.Redact(Regex.Replace(text,@"\b[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+:[A-Za-z0-9_+/=-]{16,}","[ключ скрыт]"));
    public static string Format(Exception error)
    {
        var root=error;while(root.InnerException is not null)root=root.InnerException;
        // Keep the deepest cause before the wrapper/stack. Redact each line without
        // losing the remaining exception chain to the compact UI limit of Redact.
        var report="Причина: "+root.GetType().Name+": "+Safe(root.Message)+"\n\n"+
            string.Join("\n",error.ToString().Split('\n').Select(Safe));
        return report.Length>64000?report[..64000]+"…":report;
    }
}
