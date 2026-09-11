namespace WinLogRotate.Gui.Ui;

/// <summary>
/// Runs work on the UI thread, or drops it if the control it was for has gone.
/// </summary>
/// <remarks>
/// <para>
/// <c>BeginInvoke</c> needs a created handle and a live control, and a line arriving from a
/// running child can reach a page the user has already navigated away from -
/// <c>MainForm.ShowPage</c> clears the content panel, which detaches the old page and destroys
/// its handle. The same hazard applies to a form closed before an installer's polite request
/// reaches it.
/// </para>
/// <para>
/// This shape is <c>Program.Close</c>'s, promoted. That method was the only place in the GUI
/// that got it right, and it is now a caller - so the helper is exercised by the path that
/// always worked rather than only by the two that did not.
/// </para>
/// </remarks>
internal static class UiThread
{
    /// <summary>
    /// Runs <paramref name="action"/> on the thread that owns <paramref name="control"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the fix, and each step is load-bearing:
    /// </para>
    /// <para>
    /// 1. A disposed control is gone; there is nothing to post to.
    /// </para>
    /// <para>
    /// 2. <b><c>InvokeRequired</c> is only trustworthy once a handle exists.</b> It walks up the
    /// parent chain looking for a control with a created handle and answers <c>false</c> when it
    /// finds none - so a detached page takes the "already on the UI thread" branch and touches a
    /// destroyed control from a thread-pool thread. That is why the guard cannot simply be
    /// <c>InvokeRequired</c>, which is what it was.
    /// </para>
    /// <para>
    /// 3. Already on the UI thread: run it inline, so callers that were synchronous stay
    /// synchronous and their ordering does not change.
    /// </para>
    /// <para>
    /// 4. The handle can still be destroyed between the check and the post. That race cannot be
    /// closed from here, so it is caught.
    /// </para>
    /// <para>
    /// Work for a control that has gone is dropped, deliberately: nobody is looking at it, the
    /// operation it was reporting on carries on regardless, and what it was saying is in the
    /// journal and the Event Log.
    /// </para>
    /// </remarks>
    public static void Post(Control control, Action action)
    {
        if (control.IsDisposed || !control.IsHandleCreated)
        {
            return;
        }

        if (!control.InvokeRequired)
        {
            action();
            return;
        }

        try
        {
            control.BeginInvoke(action);
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// A line callback for <c>CliRunner</c>, safe to hand to a child that outlives its page.
    /// </summary>
    /// <remarks>
    /// Guarding the control that will actually be written to, not its parent: a page can be
    /// detached while the text box inside it is what the callback touches.
    /// </remarks>
    public static Action<string> LineTo(Control control, Action<string> append) =>
        line => Post(control, () => append(line));
}
