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
            //DisplayNameEntry.Text = settings.DisplayName;
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
            if (string.IsNullOrWhiteSpace(ServerEntry.Text))
            {
                StatusLabel.IsVisible = true;
                StatusLabel.TextColor = Colors.OrangeRed;
                StatusLabel.Text = "Le serveur est obligatoire.";
                return;
            }

            StatusLabel.IsVisible = true;
            StatusLabel.TextColor = Colors.Gray;
            StatusLabel.Text = "Test de connexion en cours...";

            var result = await SmbSettingsStore.TestConnectionAsync(BuildCurrentSettings());

            StatusLabel.TextColor = result.Success ? Colors.Green : Colors.Red;
            StatusLabel.Text = result.Success
                ? $"✅ Connexion SMB réussie\n{result.Message}"
                : $"❌ Connexion SMB échouée\n{result.Message}";
        }

        private SmbSettings BuildCurrentSettings()
            => new()
            {
                Server = ServerEntry.Text?.Trim() ?? string.Empty,
                UserName = AnonymousCheckBox.IsChecked ? string.Empty : (UserNameEntry.Text ?? string.Empty),
                Password = AnonymousCheckBox.IsChecked ? string.Empty : (PasswordEntry.Text ?? string.Empty),
                IsAnonymous = AnonymousCheckBox.IsChecked,
               // DisplayName = DisplayNameEntry.Text ?? string.Empty
            };
    }
}
