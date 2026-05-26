using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcadDxfExport.Core;

namespace AcadDxfExport.UI
{
    public partial class ExporterWindow : Window
    {
        private readonly Database _db;

        public ExporterWindow()
        {
            InitializeComponent();
            _db = HostApplicationServices.WorkingDatabase;
            Loaded += ExporterWindow_Loaded;
        }

        private void ExporterWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("Načítání výkresu a vyhledávání rámečků...");
                var blocks = ExportEngine.GetBlockNamesInModelSpace(_db);

                if (blocks.Count == 0)
                {
                    Log("Chyba: V modelovém prostoru nebyly nalezeny žádné instance bloků.");
                    MessageBox.Show("V modelovém prostoru nebyly nalezeny žádné instance bloků.", "Upozornění", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                foreach (var block in blocks)
                {
                    CboBlockName.Items.Add(block);
                }

                // Nastavíme předvybraný rámeček, např. A4_SUMO pokud existuje
                int defaultBlockIdx = 0;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (string.Equals(blocks[i], "A4_SUMO", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultBlockIdx = i;
                        break;
                    }
                }
                CboBlockName.SelectedIndex = defaultBlockIdx;

                // Načtení všech tagů atributů v celém výkresu (pro podporu vnořených razítek)
                var tags = ExportEngine.GetAllAttributeTags(_db);
                if (tags.Count == 0)
                {
                    Log("Upozornění: Ve výkresu nebyly nalezeny žádné definice atributů.");
                    CboAttributeTag1.IsEnabled = false;
                    CboAttributeTag2.IsEnabled = false;
                }
                else
                {
                    CboAttributeTag1.IsEnabled = true;
                    CboAttributeTag2.IsEnabled = true;

                    // Pro druhý combobox přidáme na začátek možnost "[Žádný]"
                    CboAttributeTag2.Items.Add("[Žádný]");

                    foreach (var tag in tags)
                    {
                        CboAttributeTag1.Items.Add(tag);
                        CboAttributeTag2.Items.Add(tag);
                    }

                    // Nastavíme výchozí vyhledávaný tag "CISLO_VYKRESU" pro Atribut 1
                    int defaultTagIdx1 = -1;
                    for (int i = 0; i < tags.Count; i++)
                    {
                        if (string.Equals(tags[i], "CISLO_VYKRESU", StringComparison.OrdinalIgnoreCase))
                        {
                            defaultTagIdx1 = i;
                            break;
                        }
                    }
                    CboAttributeTag1.SelectedIndex = defaultTagIdx1 >= 0 ? defaultTagIdx1 : 0;

                    // Nastavíme výchozí vyhledávaný tag "LIST" pro Atribut 2
                    int defaultTagIdx2 = 0; // index 0 je "[Žádný]"
                    for (int i = 0; i < tags.Count; i++)
                    {
                        if (string.Equals(tags[i], "LIST", StringComparison.OrdinalIgnoreCase))
                        {
                            defaultTagIdx2 = i + 1; // +1 kvůli "[Žádný]"
                            break;
                        }
                    }
                    CboAttributeTag2.SelectedIndex = defaultTagIdx2;
                }

                Log("Výkres byl úspěšně analyzován.");
            }
            catch (Exception ex)
            {
                Log($"Chyba při načítání bloků: {ex.Message}");
            }
        }

        private void CboBlockName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ponecháváme bez akce, protože seznam atributů je globální pro celý výkres,
            // což umožňuje vybrat rámeček bez atributů (např. A4_SUMO)
            // a spárovat jej s libovolným tagem (např. CISLO_VYKRESU z bloku RAZITKO-SUMO).
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Vyberte cílovou složku pro exportované DXF výkresy";
                dialog.ShowNewFolderButton = true;
                
                if (Directory.Exists(TxtOutputFolder.Text))
                {
                    dialog.SelectedPath = TxtOutputFolder.Text;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TxtOutputFolder.Text = dialog.SelectedPath;
                }
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (CboBlockName.SelectedItem == null)
            {
                MessageBox.Show("Vyberte prosím blok rámečku.", "Chyba", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (CboAttributeTag1.SelectedItem == null)
            {
                MessageBox.Show("Vyberte prosím primární atribut (Atribut 1).", "Chyba", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string blockName = CboBlockName.SelectedItem.ToString();
            string attributeTag1 = CboAttributeTag1.SelectedItem.ToString();
            string attributeTag2 = CboAttributeTag2.SelectedItem?.ToString() ?? "[Žádný]";
            string outputFolder = TxtOutputFolder.Text.Trim();
            bool translateToOrigin = ChkTranslateToOrigin.IsChecked == true;

            if (string.IsNullOrEmpty(outputFolder))
            {
                MessageBox.Show("Zadejte platnou cílovou složku.", "Chyba", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Zakázání UI ovládacích prvků během práce
            SetUiState(false);
            TxtLog.Clear();

            try
            {
                Log("Zahájení exportu...");

                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    Log("Chyba: Není aktivní žádný dokument.");
                    return;
                }

                using (doc.LockDocument())
                {
                    ExportEngine.ExportFrames(
                        _db,
                        blockName,
                        attributeTag1,
                        attributeTag2,
                        outputFolder,
                        translateToOrigin,
                        logMessage => {
                            Log(logMessage);
                            AllowUIToUpdate();
                        },
                        (current, total) => {
                            UpdateProgress(current, total);
                            AllowUIToUpdate();
                        }
                    );
                }

                MessageBox.Show("Export byl úspěšně dokončen!", "Hotovo", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Kritická chyba při exportu: {ex.Message}");
                MessageBox.Show($"Během exportu došlo k chybě:\n{ex.Message}", "Chyba", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUiState(true);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Log(string message)
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
            LogScroll.ScrollToEnd();
        }

        private void UpdateProgress(int current, int total)
        {
            if (total <= 0)
            {
                ProgressExport.Value = 0;
                LblProgressPercent.Text = "0 %";
                LblProgress.Text = "Žádné položky k exportu";
                return;
            }

            double percent = ((double)current / total) * 100;
            ProgressExport.Value = percent;
            LblProgressPercent.Text = $"{Math.Round(percent)} %";
            LblProgress.Text = $"Zpracovává se: {current} z {total}";
        }

        private void SetUiState(bool enabled)
        {
            CboBlockName.IsEnabled = enabled;
            CboAttributeTag1.IsEnabled = enabled && CboAttributeTag1.Items.Count > 0;
            CboAttributeTag2.IsEnabled = enabled && CboAttributeTag2.Items.Count > 0;
            TxtOutputFolder.IsEnabled = enabled;
            this.IsEnabled = enabled;
        }

        /// <summary>
        /// Vynutí překreslení WPF UI a vyprázdnění zpráv ve smyčce (WPF Dispatcher Frame pumping).
        /// Zabraňuje zamrznutí UI při provádění náročné databázové operace na hlavním vlákně.
        /// </summary>
        private static void AllowUIToUpdate()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new DispatcherOperationCallback(f =>
                {
                    ((DispatcherFrame)f).Continue = false;
                    return null;
                }), frame);
            Dispatcher.PushFrame(frame);
        }
    }
}
