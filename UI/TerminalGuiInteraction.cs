using Terminal.Gui;

namespace mykeepass.UI;

/// <summary>
/// TUI implementation of <see cref="IUserInteraction"/>.
/// <see cref="WriteLine"/> and <see cref="WriteError"/> append to the history panel.
/// All interactive prompts open Terminal.Gui modal dialogs.
/// </summary>
internal sealed class TerminalGuiInteraction(Action<string> appendHistory) : IUserInteraction
{
    public void WriteLine(string text = "")
        => Application.Invoke(() => appendHistory(text));

    public void WriteError(string text)
        => Application.Invoke(() => appendHistory($"[!] {text}"));

    public string Prompt(string message, string defaultValue = "")
    {
        string result = defaultValue;

        var dialog = new Dialog { Title = message, Width = 60, Height = 9 };

        var label = new Label { Text = message, X = 1, Y = 1 };
        var field = new TextField
        {
            X = 1, Y = 3,
            Width = Dim.Fill(2),
            Text = defaultValue,
        };

        var okBtn = new Button { Text = "OK" };
        okBtn.Accepting += (_, _) =>
        {
            result = field.Text ?? defaultValue;
            dialog.RequestStop();
        };

        var cancelBtn = new Button { Text = "Cancel" };
        cancelBtn.Accepting += (_, _) =>
        {
            result = defaultValue;
            dialog.RequestStop();
        };

        dialog.Add(label, field);
        dialog.AddButton(cancelBtn);
        dialog.AddButton(okBtn);

        Application.Run(dialog);
        dialog.Dispose();
        return result;
    }

    public string ReadPassword(string prompt = "")
    {
        string result = "";
        string title  = string.IsNullOrWhiteSpace(prompt)
            ? "Password"
            : prompt.TrimEnd(':', ' ');

        var dialog = new Dialog { Title = title, Width = 60, Height = 9 };

        var label = new Label { Text = "Enter value (hidden):", X = 1, Y = 1 };
        var field = new TextField
        {
            X = 1, Y = 3,
            Width = Dim.Fill(2),
            Secret = true,
        };

        var okBtn = new Button { Text = "OK" };
        okBtn.Accepting += (_, _) =>
        {
            result = field.Text ?? "";
            dialog.RequestStop();
        };

        var cancelBtn = new Button { Text = "Cancel" };
        cancelBtn.Accepting += (_, _) => dialog.RequestStop();

        dialog.Add(label, field);
        dialog.AddButton(cancelBtn);
        dialog.AddButton(okBtn);

        Application.Run(dialog);
        dialog.Dispose();
        return result;
    }

    public bool Confirm(string question)
        => MessageBox.Query("Confirm", question, "Yes", "No") == 0;

    public int PickFromList(string prompt, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return -1;

        int result = -1;
        int dialogHeight = Math.Min(items.Count + 7, 22);

        var dialog = new Dialog { Title = prompt, Width = 70, Height = dialogHeight };

        var listView = new ListView
        {
            X = 1, Y = 1,
            Width  = Dim.Fill(2),
            Height = Dim.Fill(3),
        };
        listView.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(items));

        var okBtn = new Button { Text = "OK" };
        okBtn.Accepting += (_, _) =>
        {
            result = listView.SelectedItem + 1;
            dialog.RequestStop();
        };

        var cancelBtn = new Button { Text = "Cancel" };
        cancelBtn.Accepting += (_, _) => dialog.RequestStop();

        dialog.Add(listView);
        dialog.AddButton(cancelBtn);
        dialog.AddButton(okBtn);

        Application.Run(dialog);
        dialog.Dispose();
        return result;
    }
}
