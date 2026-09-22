namespace HshVisionLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var x=Convert.ToBase64String(System.IO.File.ReadAllBytes("c:\\plate.jpg"));

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(deferStartupInitialization: true));
    }
}
