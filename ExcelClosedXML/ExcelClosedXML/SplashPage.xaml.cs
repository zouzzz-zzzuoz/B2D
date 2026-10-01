namespace ExcelClosedXML
{
    public partial class SplashPage : ContentPage
    {
        private bool _navigated;
        private CancellationTokenSource? _navigationCts;

        public SplashPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (_navigated)
                return;

            _navigationCts?.Cancel();
            _navigationCts?.Dispose();
            _navigationCts = new CancellationTokenSource();

            try
            {
                await Task.Delay(1400, _navigationCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _navigated = true;
            var window = Application.Current?.Windows.FirstOrDefault();
            if (window != null)
                window.Page = new AppShell();
        }

        protected override void OnDisappearing()
        {
            _navigationCts?.Cancel();
            _navigationCts?.Dispose();
            _navigationCts = null;
            base.OnDisappearing();
        }
    }
}
