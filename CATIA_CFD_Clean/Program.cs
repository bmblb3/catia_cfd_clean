using System.Windows.Forms;

namespace CATIA_CFD_Clean;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--id-spike"))
        {
            CatiaIdSpike.Run();
            return;
        }

        if (args.Contains("--migrate-names"))
        {
            CatiaMigrateNames.Run(apply: args.Contains("--apply"));
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
