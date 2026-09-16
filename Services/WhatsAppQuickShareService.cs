using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SIGFUR.Wpf.Services;

public sealed record WhatsAppQuickShareResult(int PaystubCount, bool UsedArchive, string WindowTitle);

public static class WhatsAppQuickShareService
{
    private const int SeparatePdfLimit = 5;
    private const int SwRestore = 9;
    private const uint KeyEventKeyUp = 0x0002;
    private const byte VkControl = 0x11;
    private const byte VkV = 0x56;
    private const uint GaRoot = 2;

    public static async Task<WhatsAppQuickShareResult> PasteAsync(
        IReadOnlyList<string> files,
        int requestedMilitaryCount,
        string message,
        int month,
        int year,
        CancellationToken cancellationToken = default)
    {
        var useArchive = requestedMilitaryCount > SeparatePdfLimit;
        var preparedFiles = PaystubClipboardService.PrepareFilesForSharing(
            files,
            useArchive ? 0 : SeparatePdfLimit,
            $"Contracheques - {month:00}-{year}.zip");

        var target = FindTopmostWhatsAppWindow();
        if (target.Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "Abra o WhatsApp, entre na conversa desejada e deixe essa janela aberta antes de clicar em Compartilhar rápido.");

        await ActivateAsync(target.Handle, cancellationToken);

        for (var index = 0; index < preparedFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PaystubClipboardService.CopyFilesOnly([preparedFiles[index]]);
            EnsureTargetIsForeground(target.Handle);
            PasteFromClipboard();
            await Task.Delay(index == 0 ? 450 : 220, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            await Task.Delay(280, cancellationToken);
            PaystubClipboardService.CopyTextOnly(message);
            EnsureTargetIsForeground(target.Handle);
            PasteFromClipboard();
        }

        return new WhatsAppQuickShareResult(files.Count, useArchive, target.Title);
    }

    private static (IntPtr Handle, string Title) FindTopmostWhatsAppWindow()
    {
        var result = (Handle: IntPtr.Zero, Title: string.Empty);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            var title = WindowTitle(window);
            if (!title.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return true;

            GetWindowThreadProcessId(window, out var processId);
            try
            {
                var processName = Process.GetProcessById((int)processId).ProcessName;
                var supported = processName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase)
                                || processName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
                                || processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                                || processName.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                                || processName.Equals("brave", StringComparison.OrdinalIgnoreCase)
                                || processName.Equals("opera", StringComparison.OrdinalIgnoreCase);
                if (!supported) return true;
            }
            catch { return true; }

            result = (window, title);
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static async Task ActivateAsync(IntPtr window, CancellationToken cancellationToken)
    {
        _ = ShowWindow(window, SwRestore);
        _ = BringWindowToTop(window);
        _ = SetForegroundWindow(window);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTargetForeground(window)) return;
            await Task.Delay(40, cancellationToken);
            _ = BringWindowToTop(window);
            _ = SetForegroundWindow(window);
        }
        throw new InvalidOperationException("Não foi possível trazer a janela do WhatsApp para frente. Abra a conversa e tente novamente.");
    }

    private static void EnsureTargetIsForeground(IntPtr window)
    {
        if (IsTargetForeground(window)) return;
        _ = BringWindowToTop(window);
        _ = SetForegroundWindow(window);
        if (!IsTargetForeground(window))
            throw new InvalidOperationException("A janela do WhatsApp perdeu o foco antes da colagem. Tente novamente sem trocar de janela.");
    }

    private static bool IsTargetForeground(IntPtr target)
    {
        var foreground = GetForegroundWindow();
        return foreground == target || GetAncestor(foreground, GaRoot) == target;
    }

    private static void PasteFromClipboard()
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkV, 0, 0, UIntPtr.Zero);
        keybd_event(VkV, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static string WindowTitle(IntPtr window)
    {
        var builder = new StringBuilder(GetWindowTextLength(window) + 1);
        _ = GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
