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

        // Created empty, hardened, and only then written. The order is the whole point: this used
        // to write the content, close the handle, and harden afterwards - so between those two
        // steps a complete copy of the file sat on disk under a predictable name, carrying
        // whatever the directory hands out, which for the configuration directory is read access
        // for every local user. For the secret store that is the ciphertext.
        //
        // Hardening cannot be done while the handle is held: the descriptor is set through a
        // second open, and FileShare.None - which is correct for the write - denies it. So the
        // file is created and closed empty first. Anything that races into the gap finds nothing
        // in it.
        using (File.Create(temp))
        {
            // Closed immediately. Nothing is written here.
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

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
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
            // Best effort, and now safe to be: this is only reached when hardening failed, so
            // whatever is left behind is precisely the file that did NOT get the protection the
            // real one would have had. It is empty, because the content is written afterwards -
            // which is why the ordering above was changed. The previous excuse here, that the
            // temporary file "carries the same protection the real one would have had", was
            // false on the one path that reaches this.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
