using Wyvern.Core.Platform;

namespace Wyvern.Core.AutoExpand;

/// <summary>
/// The keyboard hook that Windows calls for every key that is pressed, in any program that
/// runs with the owner's own rights. Implemented with Windows calls in the app. It only turns
/// a key press into a <see cref="TypedKey"/> and hands it on: what is kept, and what happens,
/// is decided in <see cref="AutoExpander"/>.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Windows calls a hook on the thread that installed it, which must have
/// a message loop, and every key press in every program waits for that call to return. So the
/// hook has a thread of its own that does nothing else: it is started by
/// <see cref="Install"/> and has ended when <see cref="Remove"/> returns, and with no hook
/// there is no such thread. The members of this interface are called from one thread, the one
/// the object was made on (the UI thread); the callback given to <see cref="Install"/> is
/// called on the hook's thread, and never on the UI thread.</para>
/// <para>The callback does nothing that waits, reads or writes a file, or writes the log, and
/// it never waits for the UI thread.</para>
/// </remarks>
public interface IKeyboardHook
{
    /// <summary>How many times this run of Wyvern has installed a hook. For the log. It counts the whole run, so it is 0 only when auto-expand has not been on since Wyvern started.</summary>
    int Installs { get; }

    /// <summary>How many times this run of Wyvern has started a thread for a hook. For the log. It counts the whole run, so it is 0 only when auto-expand has not been on since Wyvern started.</summary>
    int Threads { get; }

    /// <summary>
    /// Starts the hook's thread and installs the hook on it. It returns once the hook is
    /// installed. From then on <paramref name="keyDown"/> is called, on the hook's thread, for
    /// every key that goes down. It must return at once and must not throw.
    /// </summary>
    /// <exception cref="Exception">
    /// No hook stays installed. When Windows would not install the hook, the thread has ended.
    /// When the thread did not start in time, it was given up on and may still be there: as
    /// soon as it gets to run it takes out whatever it installs, and then it ends, but until
    /// then a hook it installed may still be there, and so may the thread.
    /// </exception>
    void Install(Action<TypedKey> keyDown);

    /// <summary>
    /// Removes the hook, when one is installed, and ends its thread. When this returns the
    /// thread has ended: no call of the callback is running and none will come.
    /// </summary>
    /// <returns>
    /// False when there was no hook to give back, or when the call that gives the hook back to
    /// Windows failed. That is all a false says. Windows takes a hook away, without saying so,
    /// when its callback was once too slow, and gives a program no way to find out whether it
    /// has: so a false does not show that Windows had taken the hook away, and a true does not
    /// show that it had not.
    /// </returns>
    /// <exception cref="Exception">
    /// The thread did not end in time. What that leaves behind: the hook may still be
    /// installed and its thread may still be running, so the callback may still be called. The
    /// implementation has by then done everything it can so that both end by themselves, and
    /// this object has let go of them: it no longer knows of that hook or that thread, and a
    /// later call of this method does not try them again. <see cref="Install"/> may be called
    /// afterwards, and starts a new hook on a new thread.
    /// </exception>
    bool Remove();
}

/// <summary>
/// The window a keyword was typed in, as far as replacing the keyword needs Windows for it.
/// Implemented with Windows calls in the app. Asking either question changes nothing: no focus
/// is moved. Used on the UI thread only: a replacement is made there.
/// </summary>
public interface ITypingTarget
{
    /// <summary>
    /// True when that window is the one in front at this moment and is one a text can be
    /// pasted into: not one of Wyvern's own, not the desktop, not the taskbar.
    /// </summary>
    bool IsInFront(nint window);

    /// <summary>
    /// True while a Shift, Ctrl, Alt or Windows key is held down. A held key would turn the
    /// Backspaces and the Ctrl+V into other shortcuts: with Ctrl held, Backspace takes away a
    /// whole word.
    /// </summary>
    bool ModifierHeld();

    /// <summary>
    /// The target of one paste that takes the place of a keyword: that window, and a key press
    /// that is <paramref name="erase"/> Backspaces followed by Ctrl+V, handed to Windows
    /// together. The key press looks once more itself, and presses nothing at all unless the
    /// window is in front and no modifier key is held. So the keyword is never taken away
    /// without the paste. Its <see cref="IPasteTarget.Activate"/> moves no focus: it only says
    /// whether the window is in front, and the expander never calls it, because its own last
    /// look answers for it.
    /// </summary>
    IPasteTarget Replacing(nint window, int erase);
}
