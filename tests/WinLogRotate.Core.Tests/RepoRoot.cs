namespace WinLogRotate.Core.Tests;

/// <summary>Locates the repository root from the test binary, for tests that read source.</summary>
internal static class RepoRoot
{
    public static DirectoryInfo Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinLogRotate.slnx")))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException(
            "Could not find WinLogRotate.slnx above " + AppContext.BaseDirectory);
    }
}
