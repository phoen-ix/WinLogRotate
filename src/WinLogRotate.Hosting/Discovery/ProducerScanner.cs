using System.Runtime.Versioning;
using System.Xml.Linq;

namespace WinLogRotate.Hosting.Discovery;

/// <summary>Something on this machine that writes logs.</summary>
public sealed record ProducerFinding
{
    public required string Producer { get; init; }
    public required string Directory { get; init; }
    public required string Pattern { get; init; }

    /// <summary>Does it rotate its own logs?</summary>
    public required bool SelfRotates { get; init; }

    /// <summary>Does it ever delete the ones it rotated? Almost always no, which is the
    /// entire reason this tool exists.</summary>
    public required bool SelfDeletes { get; init; }

    public required string SuggestedKind { get; init; }
    public string? Note { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Finds log producers and says which ones leave their old logs behind.
/// </summary>
/// <remarks>
/// The scan is read-only, never follows a reparse point, and creates nothing. Its value is
/// almost entirely in the "rotates but never deletes" column: that combination describes IIS,
/// http.sys, NSSM, WinSW, Apache's rotatelogs and log4net, and it is why disks fill up with
/// perfectly correctly-rotated files.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ProducerScanner
{
    public static IReadOnlyList<ProducerFinding> Scan()
    {
        var findings = new List<ProducerFinding>();

        findings.AddRange(ScanIis());
        findings.AddRange(ScanHttpSys());
        findings.AddRange(ScanSqlServer());

        return findings;
    }

    /// <summary>
    /// Reads IIS's own configuration for the sites and their log directories.
    /// </summary>
    /// <remarks>
    /// applicationHost.config is parsed directly rather than through
    /// <c>Microsoft.Web.Administration</c>, which is a managed wrapper over COM and would drag
    /// COM into a NativeAOT binary - and would make this GUI-only, stranding Server Core, which
    /// is where IIS is most often installed.
    /// </remarks>
    private static IEnumerable<ProducerFinding> ScanIis()
    {
        var config = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "inetsrv", "config", "applicationHost.config");

        if (!File.Exists(config))
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            yield break;
        }

        var defaultDirectory = document
            .Descendants("siteDefaults").Elements("logFile")
            .Attributes("directory").FirstOrDefault()?.Value
            ?? @"%SystemDrive%\inetpub\logs\LogFiles";

        foreach (var site in document.Descendants("site"))
        {
            var id = site.Attribute("id")?.Value;
            var name = site.Attribute("name")?.Value ?? "site";
            var directory = site.Element("logFile")?.Attribute("directory")?.Value ?? defaultDirectory;

            var expanded = Environment.ExpandEnvironmentVariables(directory);
            var siteDirectory = id is null ? expanded : Path.Combine(expanded, "W3SVC" + id);

            var (count, bytes) = Measure(siteDirectory, "u_ex*.log");

            yield return new ProducerFinding
            {
                Producer = $"IIS - {name}",
                Directory = siteDirectory,
                Pattern = Path.Combine(siteDirectory, "u_ex*.log").Replace('\\', '/'),
                SelfRotates = true,
                SelfDeletes = false,
                SuggestedKind = "manage",
                Note = "IIS rolls these itself and never deletes them. It also has no way to "
                     + "reopen the current file - appcmd site stop/start does not release the "
                     + "W3SVC handle, only iisreset does - so the live file must never be touched.",
                FileCount = count,
                TotalBytes = bytes,
            };
        }
    }

    private static IEnumerable<ProducerFinding> ScanHttpSys()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "LogFiles", "HTTPERR");

        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var (count, bytes) = Measure(directory, "httperr*.log");

        yield return new ProducerFinding
        {
            Producer = "http.sys (HTTPERR)",
            Directory = directory,
            Pattern = Path.Combine(directory, "httperr*.log").Replace('\\', '/'),
            SelfRotates = true,
            SelfDeletes = false,
            SuggestedKind = "manage",
            Note = "Held open by http.sys, which does not permit renaming. Rolled by size and "
                 + "never cleaned up.",
            FileCount = count,
            TotalBytes = bytes,
        };
    }

    private static IEnumerable<ProducerFinding> ScanSqlServer()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");

        if (key is null)
        {
            yield break;
        }

        foreach (var instance in key.GetValueNames())
        {
            if (key.GetValue(instance) is not string internalName)
            {
                continue;
            }

            using var parameters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Microsoft SQL Server\{internalName}\MSSQLServer\Parameters");

            var errorLog = parameters?.GetValueNames()
                .Select(n => parameters.GetValue(n) as string)
                .FirstOrDefault(v => v?.StartsWith("-e", StringComparison.OrdinalIgnoreCase) == true)?[2..];

            if (errorLog is null)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(errorLog);
            if (directory is null || !Directory.Exists(directory))
            {
                continue;
            }

            var (count, bytes) = Measure(directory, "ERRORLOG*");

            yield return new ProducerFinding
            {
                Producer = $"SQL Server - {instance}",
                Directory = directory,
                Pattern = Path.Combine(directory, "ERRORLOG.*").Replace('\\', '/'),
                SelfRotates = true,
                SelfDeletes = false,
                SuggestedKind = "manage",
                Note = "SQL Server cycles its error log on restart or on sp_cycle_errorlog, and "
                     + "keeps NumErrorLogs of them (6 by default). It has no scheduler of its "
                     + "own on Express, so an external trigger is genuinely useful here.",
                FileCount = count,
                TotalBytes = bytes,
            };
        }
    }

    private static (int Count, long Bytes) Measure(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        try
        {
            var files = new DirectoryInfo(directory).GetFiles(pattern, new EnumerationOptions
            {
                MatchType = MatchType.Simple,
                AttributesToSkip = FileAttributes.None,
                IgnoreInaccessible = true,
                // Never descend into a junction: any user can create one, and a scan running
                // elevated must not be steerable.
                RecurseSubdirectories = false,
            });

            return (files.Length, files.Sum(f => f.Length));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return (0, 0);
        }
    }
}
