using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ConnectionHub.Services;
using ConnectionHub.Ssh;

namespace ConnectionHub.Views;

/// <summary>Generic credential dialog used for SSH keyboard-interactive (2FA / OTP) prompts and passwords.</summary>
public partial class PromptWindow : Window
{
    private readonly List<Control> _inputs = new();

    public PromptWindow(string target, string header, string instruction, IReadOnlyList<AuthPrompt> prompts, string? initialValue = null)
    {
        InitializeComponent();
        WindowTheming.Attach(this);

        HeaderText.Text = header;
        TargetText.Text = target;
        InstructionText.Text = instruction.Trim();
        InstructionText.Visibility = string.IsNullOrWhiteSpace(instruction) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var prompt in prompts)
        {
            Fields.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(prompt.Text) ? "Antwoord:" : prompt.Text,
                Style = (Style)FindResource("FieldLabel"),
                TextWrapping = TextWrapping.Wrap
            });

            Control input = prompt.Echo ? new TextBox { Text = _inputs.Count == 0 ? initialValue ?? string.Empty : string.Empty } : new PasswordBox();
            _inputs.Add(input);
            Fields.Children.Add(input);
        }

        Loaded += (_, _) =>
        {
            var first = _inputs.FirstOrDefault();
            first?.Focus();
            if (first is TextBox box && box.Text.Length > 0)
            {
                // Select the name without its extension, like Windows Explorer does when renaming.
                var dot = box.Text.LastIndexOf('.');
                box.Select(0, dot > 0 ? dot : box.Text.Length);
            }
        };
    }

    public string[] Answers { get; private set; } = Array.Empty<string>();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Answers = _inputs.Select(i => i switch
        {
            PasswordBox p => p.Password,
            TextBox t => t.Text,
            _ => string.Empty
        }).ToArray();
        DialogResult = true;
    }
}
