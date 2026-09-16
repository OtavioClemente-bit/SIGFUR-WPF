using System.Windows;
using SIGFUR.Wpf.Views.Shared;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Substitui caixas nativas do Windows pelo padrão visual do SIGFUR, preservando
/// as assinaturas mais usadas de MessageBox para facilitar adoção em todos os módulos.
/// </summary>
public static class SigfurDialog
{
    public static MessageBoxResult Show(string messageBoxText)
        => Show(null, messageBoxText, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption)
        => Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
        => Show(null, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        => Show(null, messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
        => Show(null, messageBoxText, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(Window owner, string messageBoxText)
        => Show(owner, messageBoxText, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption)
        => Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button)
        => Show(owner, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        => Show(owner, messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        try
        {
            var app = Application.Current;
            if (app is null || app.Dispatcher.HasShutdownStarted)
                return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);

            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Show(owner, messageBoxText, caption, button, icon, defaultResult));

            var dialog = new SigfurDialogWindow(messageBoxText, caption, button, icon, defaultResult);
            var resolvedOwner = owner ?? app.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? app.MainWindow;
            if (resolvedOwner is { IsLoaded: true } && !ReferenceEquals(resolvedOwner, dialog))
                dialog.Owner = resolvedOwner;
            dialog.ShowDialog();
            return dialog.Result == MessageBoxResult.None ? ResolveFallback(button, defaultResult) : dialog.Result;
        }
        catch
        {
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
        }
    }

    private static MessageBoxResult ResolveFallback(MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        if (defaultResult != MessageBoxResult.None) return defaultResult;
        return buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
    }
}
