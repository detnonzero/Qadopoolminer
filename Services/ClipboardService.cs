using System.Windows;

namespace Qadopoolminer.Services;

public sealed class ClipboardService
{
    public void SetText(string text)
        => Clipboard.SetText(text);
}
