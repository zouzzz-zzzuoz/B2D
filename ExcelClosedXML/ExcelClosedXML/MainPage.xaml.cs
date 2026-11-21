using System.Globalization;
using System.Reflection;
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
            var assembly = typeof(App).Assembly;

            var buildTimestampUtc = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildTimestampUtc")
                ?.Value;

            if (DateTimeOffset.TryParse(buildTimestampUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                return dto.LocalDateTime.ToString("'le' dd/MM/yy 'à' HH'h'mm'm'ss's'", culture);
            }

            var assemblyPath = assembly.Location;
            var fallbackBuildTime = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath)
                ? File.GetLastWriteTime(assemblyPath)
                : Directory.GetLastWriteTime(AppContext.BaseDirectory);

            return fallbackBuildTime.ToString("'le' dd/MM/yy 'à' HH'h'mm'm'ss's'", culture);
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
