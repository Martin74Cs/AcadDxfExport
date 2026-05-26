using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcadDxfExport.UI;

// Registrace assembly pro AutoCAD (volitelné, ale doporučené pro správné načtení příkazů)
[assembly: CommandClass(typeof(AcadDxfExport.CommandEntry))]

namespace AcadDxfExport
{
    public class CommandEntry
    {
        /// <summary>
        /// Deklarace příkazu EXPORTFRAMES pro AutoCAD.
        /// Příkaz otevře WPF okno exportéru rámečků.
        /// </summary>
        [CommandMethod("EXPORTFRAMES")]
        public void ShowExportFramesWindow()
        {
            try
            {
                var window = new ExporterWindow();
                
                // Zobrazení WPF okna jako modálního okna AutoCADu.
                // Tento způsob správně předává fokus a zamyká hlavní okno AutoCADu.
                Application.ShowModalWindow(window);
            }
            catch (System.Exception ex)
            {
                // V případě chyby vypíšeme zprávu do příkazové řádky AutoCADu.
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                if (ed != null)
                {
                    ed.WriteMessage($"\nChyba při spouštění EXPORTFRAMES: {ex.Message}\n");
                }
            }
        }
    }
}
