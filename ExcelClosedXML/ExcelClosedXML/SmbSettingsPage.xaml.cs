using ExcelClosedXML.Services;

namespace ExcelClosedXML
{
    public partial class SmbSettingsPage : ContentPage
    {
        public SmbSettingsPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            var settings = await SmbSettingsStore.LoadAsync();
            ServerEntry.Text = settings.Server;
            UserNameEntry.Text = settings.UserName;
            PasswordEntry.Text = settings.Password;
            AnonymousCheckBox.IsChecked = settings.IsAnonymous;
        }

        private async void OnCancelClicked(object? sender, EventArgs e)
            => await Navigation.PopModalAsync();

        private async void OnOkClicked(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ServerEntry.Text))
            {
                await DisplayAlertAsync("Paramètres SMB", "Le serveur est obligatoire.", "OK");
                return;
            }

            await SmbSettingsStore.SaveAsync(BuildCurrentSettings());
            await Navigation.PopModalAsync();
        }

        private async void OnTestClicked(object? sender, EventArgs e)
        {
            SpeedLabel.IsVisible = false;
            SpeedLabel.Text = string.Empty;

            if (string.IsNullOrWhiteSpace(ServerEntry.Text))
            {
                StatusLabel.IsVisible = true;
                StatusLabel.TextColor = Colors.OrangeRed;
                StatusLabel.Text = "Le serveur est obligatoire.";
                return;
            }

            StatusLabel.IsVisible = true;
            StatusLabel.TextColor = Colors.Gray;
            StatusLabel.Text = "Test de connexion et de débit en cours...";

            var result = await SmbSettingsStore.TestConnectionAsync(BuildCurrentSettings());

            // Le message du store contient un bloc "connexion" puis, après une ligne vide,
            // le bloc "débit" : on affiche le second dans SpeedLabel, sous le texte vert.
            var message = result.Message ?? string.Empty;
            var separatorIndex = message.IndexOf("\n\n", StringComparison.Ordinal);
            var connectionPart = separatorIndex >= 0 ? message[..separatorIndex] : message;
            var speedPart = separatorIndex >= 0 ? message[(separatorIndex + 2)..] : string.Empty;

            StatusLabel.TextColor = result.Success ? Colors.Green : Colors.Red;
            StatusLabel.Text = result.Success
                ? $"✅ Connexion SMB réussie\n{connectionPart}"
                : $"❌ Connexion SMB échouée\n{connectionPart}";

            if (result.Success && !string.IsNullOrWhiteSpace(speedPart))
            {
                SpeedLabel.FormattedText = BuildSpeedText(speedPart);
                SpeedLabel.IsVisible = true;
            }
        }

        private static Color RatingColor(string rating) => rating switch
        {
            "EXCELLENT" => Color.FromArgb("#006400"),   // vert foncé
            "très bon" or "bon" => Colors.Green,         // vert
            "passable" or "lent" => Colors.Orange,       // orange
            _ => Colors.Red                              // TROP LENT
        };

        /// <summary>Texte du débit : chaque ligne se termine par " ---> appréciation" colorée.</summary>
        private FormattedString BuildSpeedText(string text)
        {
            var formatted = new FormattedString();
            var lines = text.Split('\n');
            const string marker = " ---> ";

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var idx = line.IndexOf(marker, StringComparison.Ordinal);

                if (idx >= 0)
                {
                    var rating = line[(idx + marker.Length)..];
                    formatted.Spans.Add(new Span { Text = line[..idx] });
                    formatted.Spans.Add(new Span
                    {
                        Text = marker + rating,
                        TextColor = RatingColor(rating),
                        FontAttributes = FontAttributes.Bold
                    });
                }
                else
                {
                    formatted.Spans.Add(new Span { Text = line });
                }

                if (i < lines.Length - 1)
                    formatted.Spans.Add(new Span { Text = "\n" });
            }

            return formatted;
        }

        private SmbSettings BuildCurrentSettings()
            => new()
            {
                Server = ServerEntry.Text?.Trim() ?? string.Empty,
                UserName = AnonymousCheckBox.IsChecked ? string.Empty : (UserNameEntry.Text ?? string.Empty),
                Password = AnonymousCheckBox.IsChecked ? string.Empty : (PasswordEntry.Text ?? string.Empty),
                IsAnonymous = AnonymousCheckBox.IsChecked,
            };
    }
}