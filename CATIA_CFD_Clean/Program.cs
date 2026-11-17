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

        // Catalogues the active part like the Re-catalogue button (without pruning app state). Prints the result.
        if (args.Contains("--catalogue"))
        {
            var r = CatiaCatalogue.Run(CatiaConnection.GetActiveDocument());
            Console.WriteLine($"Catalogued {r.Bodies} bodies, {r.DuplicateNames} shared name(s) -> {r.DbPath}");
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
