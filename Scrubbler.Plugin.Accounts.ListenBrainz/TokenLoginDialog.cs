using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scrubbler.PluginBase.Services;

namespace Scrubbler.Plugin.Accounts.ListenBrainz;

internal sealed class TokenLoginDialog(IDialogService dialogs, ILinkOpenerService linkOpener)
{
    public async Task ShowAsync(Func<string, Task<string?>> login)
    {
        var token = new PasswordBox { Header = "User token", MinWidth = 320 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var settingsLink = new HyperlinkButton { Content = "Open ListenBrainz settings" };
        settingsLink.Click += async (_, _) =>
        {
            try { await linkOpener.OpenLink("https://listenbrainz.org/settings/"); }
            catch
            {
                error.Text = "Open https://listenbrainz.org/settings/ in your browser to find your user token.";
                error.Visibility = Visibility.Visible;
            }
        };
        var content = new StackPanel { Spacing = 12, MaxWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = "Copy your user token from ListenBrainz settings and paste it below.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(settingsLink);
        content.Children.Add(token);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Connect to ListenBrainz",
            Content = content,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        var isConnecting = false;
        token.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(token.Password);
        dialog.Closing += (_, args) => args.Cancel = isConnecting;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            isConnecting = true;
            token.IsEnabled = false;
            settingsLink.IsEnabled = false;
            try
            {
                var message = await login(token.Password);
                args.Cancel = message != null;
                error.Text = message ?? "";
                error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
            }
            finally
            {
                isConnecting = false;
                token.IsEnabled = true;
                settingsLink.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(token.Password);
                deferral.Complete();
            }
        };
        try { await dialogs.ShowDialogAsync(dialog); }
        finally { token.Password = ""; }
    }
}
