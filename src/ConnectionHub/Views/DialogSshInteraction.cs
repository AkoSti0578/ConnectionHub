using System.Collections.Generic;
using System.Windows;
using ConnectionHub.Ssh;

namespace ConnectionHub.Views;

/// <summary>Shows SSH authentication dialogs on the UI thread on behalf of the (background) SSH connect logic.</summary>
public sealed class DialogSshInteraction : ISshInteraction
{
    private readonly Window? _owner;

    public DialogSshInteraction(Window? owner) => _owner = owner;

    public bool ConfirmHostKey(string host, int port, string keyType, string fingerprint, string? previousFingerprint)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            if (previousFingerprint == null)
            {
                return MessageWindow.Show(_owner,
                    "Onbekende host",
                    $"De authenticiteit van {host}:{port} kan niet worden vastgesteld.\n\n{keyType}\n{fingerprint}\n\nVertrouw je deze host en wil je de sleutel opslaan?",
                    MessageKind.Question, "Vertrouwen en verbinden", "Annuleren");
            }

            return MessageWindow.Show(_owner,
                "Hostsleutel gewijzigd!",
                $"WAARSCHUWING: de hostsleutel van {host}:{port} is gewijzigd. Dit kan duiden op een man-in-the-middle aanval.\n\n" +
                $"Opgeslagen:\n{previousFingerprint}\n\nNieuw:\n{keyType} {fingerprint}\n\nAlleen doorgaan als je zeker weet dat de sleutel legitiem is gewijzigd.",
                MessageKind.Warning, "Nieuwe sleutel accepteren", "Annuleren");
        });
    }

    public string[]? PromptKeyboardInteractive(string title, string instruction, IReadOnlyList<AuthPrompt> prompts)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new PromptWindow(title, "Verificatie vereist", instruction, prompts) { Owner = _owner };
            return dialog.ShowDialog() == true ? dialog.Answers : null;
        });
    }

    public string? PromptSecret(string title, string message)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new PromptWindow(title, title, message, new[] { new AuthPrompt("Wachtwoord:", false) }) { Owner = _owner };
            return dialog.ShowDialog() == true ? dialog.Answers[0] : null;
        });
    }
}
