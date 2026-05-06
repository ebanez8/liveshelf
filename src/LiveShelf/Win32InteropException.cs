namespace LiveShelf;

internal sealed class Win32InteropException : Exception
{
    public Win32InteropException(string message)
        : base(message)
    {
    }
}
