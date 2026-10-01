using System.Globalization;
using ExcelClosedXML.Services;

namespace ExcelClosedXML
{
    public partial class MainPage : ContentPage
    {
        private static readonly string BuildInfoText = CreateBuildInfoText();
        private readonly IFilePickerService _filePicker;
        private bool _isPicking;

        public MainPage(IFilePickerService filePicker)
        {
            InitializeComponent();
            _filePicker = filePicker;
            BuildInfoLabel.Text = BuildInfoText;
        }

        private static string CreateBuildInfoText()
        {
            var culture = CultureInfo.GetCultureInfo("fr-FR");
            var assemblyPath = typeof(App).Assembly.Location;
            var buildTime = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath)
                ? File.GetLastWriteTime(assemblyPath)
                : Directory.GetLastWriteTime(AppContext.BaseDirectory);

            return buildTime.ToString("'le' dd/MM/yy 'à' HH'h'mm'm'ss's'", culture);
        }

        private async void OnOpenFileClicked(object? sender, EventArgs e)
        {
            if (_isPicking)
                return;

            _isPicking = true;
            OpenBtn.IsEnabled = false;
            StatusLabel.Text = "Ouverture du sélecteur...";
            StatusLabel.IsVisible = true;
            await Task.Yield();

            try
            {
                var fileInfo = await _filePicker.PickExcelFileAsync();
                if (fileInfo == null)
                {
                    StatusLabel.IsVisible = false;
                    return;
                }

                StatusLabel.Text = $"Chargement de {fileInfo.FileName}...";
                NavigationData.PendingFile = fileInfo;
                await Shell.Current.GoToAsync(nameof(SpreadsheetPage));
                StatusLabel.IsVisible = false;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Erreur : {ex.Message}";
            }
            finally
            {
                _isPicking = false;
                OpenBtn.IsEnabled = true;
            }
        }

        private async void OnSmbSettingsClicked(object? sender, EventArgs e)
            => await Navigation.PushModalAsync(new SmbSettingsPage());
    }

}
