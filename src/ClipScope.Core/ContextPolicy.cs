namespace ClipScope.Core;

public static class ContextPolicy
{
    public static SourceContext ResolveClipboardSource(SourceContext owner, SourceContext foreground)
    {
        if (!owner.IsResolved) return new SourceContext();
        // Many applications use a hidden clipboard owner on the same process.
        // The foreground window supplies the user's document/window context only
        // when the owner identity confirms that it belongs to that application.
        return foreground.IsResolved && owner.ProcessId == foreground.ProcessId ? foreground : owner;
    }
}
