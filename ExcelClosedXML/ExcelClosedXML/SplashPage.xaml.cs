namespace ExcelClosedXML
{
    public partial class SplashPage : ContentPage
    {
        private bool _navigated;

        public SplashPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (_navigated)
                return;

            _navigated = true;
            await Task.Delay(1400);

            var window = Application.Current?.Windows.FirstOrDefault();
            if (window != null)
                window.Page = new AppShell();
        }
    }
}
