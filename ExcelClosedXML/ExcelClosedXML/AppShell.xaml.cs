namespace ExcelClosedXML
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute(nameof(SpreadsheetPage), typeof(SpreadsheetPage));
        }
    }
}
