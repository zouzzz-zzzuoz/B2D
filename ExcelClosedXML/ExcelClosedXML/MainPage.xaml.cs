using ExcelClosedXML.Services;

namespace ExcelClosedXML
{
    public partial class MainPage : ContentPage
    {
        private readonly IFilePickerService _filePicker;

        public MainPage(IFilePickerService filePicker)
        {
            InitializeComponent();
            _filePicker = filePicker;
        }

        private async void OnOpenFileClicked(object? sender, EventArgs e)
        {
            OpenBtn.IsEnabled = false;
            StatusLabel.Text = "Ouverture du sélecteur...";
            StatusLabel.IsVisible = true;

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
                OpenBtn.IsEnabled = true;
            }
        }
    }
}
