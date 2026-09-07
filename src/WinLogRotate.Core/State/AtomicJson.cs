namespace WinLogRotate.Core.State;

/// <summary>
/// Writes a file so that a crash leaves either the old contents or the new ones, never half of
/// either.
/// </summary>
/// <remarks>
/// Extracted from <see cref="StateStore"/> so the secret store gets the same guarantee without
/// the two sharing a document. Losing a rotation clock costs every log one interval; losing a
/// stored credential costs a support call and a password rotation. Both deserve the same write.
/// </remarks>
public static class AtomicJson
{
    /// <param name="harden">
    /// Applied to the temporary file, before the move.
    /// <para>
    /// The order matters. A file created under the configuration directory inherits that
    /// directory's inheritable ACEs - including the one granting every local user read - so
    /// permissions have to be set before the file has its final name, or there is a window in
    /// which a credential is world-readable. A same-volume <see cref="File.Move(string, string, bool)"/>
    /// keeps the explicit DACL and does not re-inherit, which is what makes that safe.
    /// </para>
    /// <para>
    /// If this throws, nothing is written at all: an unprotected secrets file is worse than a
    /// missing one.
    /// </para>
    /// </param>
    public static void Write(string path, string json, Action<string>? harden = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        try
        {
            harden?.Invoke(temp);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort. The caller is already throwing, and the temporary file carries the
            // same protection the real one would have had.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
