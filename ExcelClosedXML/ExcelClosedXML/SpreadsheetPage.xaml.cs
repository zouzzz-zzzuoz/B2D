using ClosedXML.Excel;
using ExcelClosedXML.Services;

namespace ExcelClosedXML
{
    public partial class SpreadsheetPage : ContentPage
    {
        // DTO léger : données pures lues depuis ClosedXML, sans aucune vue MAUI
        private record CellInfo(
            string Text,
            System.Drawing.Color? ExcelBgColor,
            System.Drawing.Color? FontColor,
            bool Bold,
            bool Italic,
            XLAlignmentHorizontalValues HAlign,
            XLAlignmentVerticalValues VAlign,
            int TextRotation,
            bool IsPinkZone,
            bool IsTagLink,
            int ColSpan,
            int RowSpan,
            bool IsMergedNonMaster);
        // ── Constantes Excel (colonnes 1-based) ──────────────────────────────
        private const int ColB = 2;
        private const int ColC = 3;
        private const int ColD = 4;
        private const int ColF = 6;
        private const int ColDisplayFirst = 2;   // B inclus
        private const int ColDisplayLast = 14;  // N inclus
        private const int ColGStart = 7;   // G inclus
        private const int ColMEnd = 13;  // M inclus
        private const int ColOExcl = 15;  // O exclus
        private const int RowTagMin = 8;
        private const int FreezeRow = 7;   // lignes 1-7 figées

        // ── Limites d'affichage ───────────────────────────────────────────────
        private const int MaxDisplayRows = 120;
        private const double RowHeight = 32;

        // ── Couleurs ─────────────────────────────────────────────────────────
        private static readonly Color CellPink = Color.FromArgb("#FFC0CB");
        private static readonly Color CellWhite = Colors.White;
        private static readonly Color CellHeader = Color.FromArgb("#EEEEEE");
        private static readonly Color CellBorderClr = Color.FromArgb("#CCCCCC");
        private static readonly SolidColorBrush CellBorderBrush = new(CellBorderClr);
        private static readonly Color LinkTextColor = Color.FromArgb("#FF2DF4");
        private static readonly Color PaleYellow = Color.FromArgb("#FFF9C4");

        // ── État ─────────────────────────────────────────────────────────────
        private readonly ITagFileOpener _tagFileOpener;
        private readonly IWorkbookStorageService _workbookStorageService;
        private XLWorkbook? _workbook;
        private IXLWorksheet? _worksheet;
        private ExcelFileInfo? _fileInfo;

        private readonly HashSet<(int row, int col)> _pinkCells = [];
        private readonly Dictionary<(int row, int col), Border> _cellBorders = [];
        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private MemoryStream? _workbookStream;
        private int _frozenBoundaryRow; // dernière ligne Excel de la zone figée
        private bool _isLoading;
        private int _hasPendingChanges;
        private static WeakReference<SpreadsheetPage>? _activeInstance;

        // Debounce pour éviter les clics multiples rapides : performance sur Android
        private readonly Dictionary<(int row, int col), DateTime> _lastCellClickTime = new();
        private const int DebounceMsAndroid = 150; // Délai minimal entre deux clics sur la même cellule (Android)

        private static void Log(string message) => AppLog.Write(message);

        public SpreadsheetPage(ITagFileOpener tagFileOpener, IWorkbookStorageService workbookStorageService)
        {
            InitializeComponent();
            _tagFileOpener = tagFileOpener;
            _workbookStorageService = workbookStorageService;
        }

        // ── Cycle de vie ──────────────────────────────────────────────────────
        protected override async void OnAppearing()
        {
            base.OnAppearing();
            _activeInstance = new WeakReference<SpreadsheetPage>(this);

            var info = NavigationData.PendingFile;
            if (info == null || _isLoading) return;

            NavigationData.PendingFile = null;
            _fileInfo = info;

            _isLoading = true;
            try
            {
                await LoadSpreadsheetAsync(info);
            }
            finally
            {
                _isLoading = false;
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _ = SavePendingChangesAsync();
        }

        // Optimisation Android : débounce pour éviter les clics multiples rapides
        private bool ShouldProcessCellClick(int row, int col)
        {
            var key = (row, col);
            var now = DateTime.UtcNow;
            if (_lastCellClickTime.TryGetValue(key, out var lastTime) &&
                (now - lastTime).TotalMilliseconds < DebounceMsAndroid)
                return false;
            _lastCellClickTime[key] = now;
            return true;
        }

        public static Task SaveCurrentWorkbookIfNeededAsync()
        {
            if (_activeInstance != null && _activeInstance.TryGetTarget(out var page))
                return page.SavePendingChangesAsync();

            Log("SaveCurrentWorkbookIfNeededAsync: _activeInstance introuvable (page fermée/GC) -> no-op");
            return Task.CompletedTask;
        }

        private void MarkWorkbookDirty()
        {
            Interlocked.Exchange(ref _hasPendingChanges, 1);
            Log("MarkWorkbookDirty() appelé -> _hasPendingChanges = 1");
        }

        private async Task SavePendingChangesAsync()
        {
            Log($"SavePendingChangesAsync() appelé, flag avant reset = {_hasPendingChanges}");

            if (Interlocked.CompareExchange(ref _hasPendingChanges, 0, 1) == 0)
            {
                Log("Aucun changement en attente -> sortie immédiate (pas de sauvegarde)");
                return;
            }

            Log("Changements détectés -> appel de SaveWorkbookAsync()");

            try
            {
                var savedPath = await SaveWorkbookAsync();
                if (string.IsNullOrWhiteSpace(savedPath))
                {
                    Log("SaveWorkbookAsync() a retourné null/vide -> échec, on remet le flag à 1");
                    Interlocked.Exchange(ref _hasPendingChanges, 1);
                }
                else
                {
                    Log($"Sauvegarde réussie -> {savedPath}");
                }
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION dans SaveWorkbookAsync() : {ex}");
                Interlocked.Exchange(ref _hasPendingChanges, 1);
            }
        }

        private static void AdjustHeaderRowHeights(Grid grid)
        {
            for (var i = 0; i < grid.RowDefinitions.Count; i++)
            {
                var height = grid.RowDefinitions[i].Height;
                if (!height.IsAbsolute)
                    continue;

                // Lignes 0 et 1 : 40%
                if (i == 0 && i <= 1)
                {
                    grid.RowDefinitions[i].Height = new GridLength(height.Value * 1.40, GridUnitType.Absolute);
                }
                // Lignes 2 : 30%
                if (i == 1)
                {
                    grid.RowDefinitions[i].Height = new GridLength(height.Value * 1.30, GridUnitType.Absolute);
                }
                // Lignes 3..4 : 30%
                if (i >= 2 && i < 3)
                {
                    grid.RowDefinitions[i].Height = new GridLength(height.Value * 1.30, GridUnitType.Absolute);
                }

                // Lignes 6 : 200%
                if (i == 5)
                {
                    grid.RowDefinitions[i].Height = new GridLength(height.Value * 3.00, GridUnitType.Absolute);
                }
                // Ligne 5 : +70%
                else if (i == 4)
                {
                    grid.RowDefinitions[i].Height = new GridLength(height.Value * 1.70, GridUnitType.Absolute);
                }
                // Ligne 9 et plus : +750%
                else if (i >= 8)
                { grid.RowDefinitions[i].Height = new GridLength(height.Value * 1.75, GridUnitType.Absolute); }
            }





        }

        private static void ApplyHeaderRowColors(Grid grid)
        {
            foreach (var child in grid.Children)
            {
                var rowIndex = grid.GetRow(child); // 0-based
                if (rowIndex < 0)
                    continue;

                var hasTarget = false;
                var target = Colors.Transparent;

                if (rowIndex <= 5)      // lignes 1..6
                {
                    target = PaleYellow;
                    hasTarget = true;
                }
                else if (rowIndex == 6) // ligne 7
                {
                    target = CellPink;
                    hasTarget = true;
                }

                if (!hasTarget)
                    continue;

                if (child is Border border)
                {
                    border.BackgroundColor = target;
                }
                else if (child is Layout layout)
                {
                    layout.BackgroundColor = target;
                }
                else if (child is VisualElement visual)
                {
                    visual.BackgroundColor = target;
                }
            }
        }

        private static void AdjustColumnWidths(Grid grid)
        {
            // Colonnes affichées : B..N => index 0..12
            // B (+15%) => index 0
            if (grid.ColumnDefinitions.Count > 0)
            {
                var colB = grid.ColumnDefinitions[0].Width;
                if (colB.IsAbsolute)
                    grid.ColumnDefinitions[0].Width = new GridLength(colB.Value * 1.15, GridUnitType.Absolute);
                else if (colB.IsStar)
                    grid.ColumnDefinitions[0].Width = new GridLength(colB.Value * 1.15, GridUnitType.Star);
            }




            // G..N (-20%) => index 5..12
            for (var i = 5; i <= 12 && i < grid.ColumnDefinitions.Count; i++)
            {
                var width = grid.ColumnDefinitions[i].Width;

                if (width.IsAbsolute)
                    grid.ColumnDefinitions[i].Width = new GridLength(width.Value * 0.8, GridUnitType.Absolute);
                else if (width.IsStar)
                    grid.ColumnDefinitions[i].Width = new GridLength(width.Value * 0.8, GridUnitType.Star);
            }
        }

        // ── Chargement principal (3 étapes bien séparées) ────────────────────
        private async Task LoadSpreadsheetAsync(ExcelFileInfo fileInfo)
        {
            StatusLabel.Text = "Ouverture du fichier…";
            StatusLabel.IsVisible = true;
            await Task.Yield();

            try
            {
                await using var sourceStream = await _workbookStorageService.OpenReadAsync(fileInfo)
                    ?? throw new InvalidOperationException("Impossible d'ouvrir le fichier Excel sélectionné.");

                var streamCapacity = sourceStream.CanSeek && sourceStream.Length > 0 && sourceStream.Length <= int.MaxValue
                    ? (int)sourceStream.Length
                    : 0;
                var ownedStream = streamCapacity > 0 ? new MemoryStream(streamCapacity) : new MemoryStream();
                await sourceStream.CopyToAsync(ownedStream);
                ownedStream.Position = 0;

                _workbookStream?.Dispose();
                _workbookStream = ownedStream;

                // ── Étape 1 : parsage ClosedXML sur thread de fond ────────────
                var (wb, ws) = await Task.Run(() =>
                {
                    _workbookStream.Position = 0;
                    var w = new XLWorkbook(_workbookStream);
                    return (w, w.Worksheet(1));
                });

                _workbook = wb;
                _worksheet = ws;

                // ── Étape 2 : extraction des données sur thread de fond ────────
                StatusLabel.Text = "Lecture des cellules… Attends quelques secondes…";

                // Libère visuellement l'ancienne grille avant reconstruction
                SpreadsheetScroll.Content = null;
                FrozenGrid.Children.Clear();
                FrozenGrid.RowDefinitions.Clear();
                FrozenGrid.ColumnDefinitions.Clear();
                await Task.Yield();

                var extracted = await Task.Run(() => ExtractCellData(ws));

                if (extracted.TotalRows == 0)
                {
                    StatusLabel.Text = "La feuille est vide.";
                    return;
                }

                // ── Étape 3 : construction de la grille sur le thread UI ───────
                //   La grille est créée DÉTACHÉE du ScrollView : aucune passe
                //   de layout pendant la boucle. Une seule passe au moment de
                //   SpreadsheetScroll.Content = grid.
                StatusLabel.Text = $"Affichage ({extracted.TotalRows} lignes × {extracted.TotalCols} colonnes)…";
                await Task.Yield();

                var (frozen, scrollable) = await BuildGridDetachedAsync(extracted);
                AdjustColumnWidths(frozen);
                AdjustColumnWidths(scrollable);
                AdjustHeaderRowHeights(frozen);
                ApplyHeaderRowColors(frozen);
                FrozenGrid.Children.Clear();
                FrozenGrid.RowDefinitions.Clear();
                FrozenGrid.ColumnDefinitions.Clear();
                // Copier définitions de colonnes (identiques dans les deux grilles)
                foreach (var cd in frozen.ColumnDefinitions)
                    FrozenGrid.ColumnDefinitions.Add(new ColumnDefinition(cd.Width));
                foreach (var rd in frozen.RowDefinitions)
                    FrozenGrid.RowDefinitions.Add(new RowDefinition(rd.Height));
                for (var i = frozen.Children.Count - 1; i >= 0; i--)
                {
                    if (frozen.Children[i] is not View child)
                        continue;

                    int col = Grid.GetColumn(child);
                    int row = Grid.GetRow(child);
                    int cols = Grid.GetColumnSpan(child);
                    int rows = Grid.GetRowSpan(child);
                    frozen.Remove(child);
                    Grid.SetColumn(child, col);
                    Grid.SetRow(child, row);
                    if (cols > 1) Grid.SetColumnSpan(child, cols);
                    if (rows > 1) Grid.SetRowSpan(child, rows);
                    FrozenGrid.Add(child);
                }
                SpreadsheetScroll.Content = scrollable;

                StatusLabel.IsVisible = false;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Erreur : {ex.Message}";
                StatusLabel.IsVisible = true;
            }
        }

        // ── Extraction des données (thread de fond, aucune vue MAUI créée) ───
        private record ExtractedData(
            CellInfo[,] Cells,
            int FirstRow, int FirstCol,
            int TotalRows, int TotalCols);

        private sealed record MergedCellInfo(int MasterRow, int MasterCol, int LastRow, int LastCol);

        private static ExtractedData ExtractCellData(IXLWorksheet ws)
        {
            var used = ws.RangeUsed();
            if (used == null)
                return new ExtractedData(new CellInfo[0, 0], 1, 1, 0, 0);

            int firstRow = used.FirstRow().RowNumber();
            int lastRow = Math.Min(used.LastRow().RowNumber(), firstRow + MaxDisplayRows - 1);
            int firstCol = ColDisplayFirst;
            int lastCol = Math.Min(used.LastColumn().ColumnNumber(), ColDisplayLast);

            int totalRows = lastRow - firstRow + 1;
            int totalCols = lastCol - firstCol + 1;
            var cells = new CellInfo[totalRows, totalCols];
            var mergedLookup = BuildMergedCellLookup(ws, firstRow, lastRow, firstCol, lastCol);

            for (int r = firstRow; r <= lastRow; r++)
            {
                for (int c = firstCol; c <= lastCol; c++)
                {
                    var xlCell = ws.Cell(r, c);
                    string text = GetCellText(xlCell);

                    System.Drawing.Color? xlBgColor = null;
                    try
                    {
                        if (xlCell.Style.Fill.PatternType != XLFillPatternValues.None)
                        {
                            var col = xlCell.Style.Fill.BackgroundColor.Color;
                            if (col.A > 0) xlBgColor = col;
                        }
                    }
                    catch { }

                    System.Drawing.Color? xlFontColor = null;
                    try
                    {
                        var fc = xlCell.Style.Font.FontColor.Color;
                        if (fc.A > 0 && !(fc.R == 0 && fc.G == 0 && fc.B == 0))
                            xlFontColor = fc;
                    }
                    catch { }

                    bool bold = false;
                    bool italic = false;
                    try { bold = xlCell.Style.Font.Bold; } catch { }
                    try { italic = xlCell.Style.Font.Italic; } catch { }

                    var hAlign = XLAlignmentHorizontalValues.General;
                    var vAlign = XLAlignmentVerticalValues.Bottom;
                    int textRotation = 0;
                    try { hAlign = xlCell.Style.Alignment.Horizontal; } catch { }
                    try { vAlign = xlCell.Style.Alignment.Vertical; } catch { }
                    try { textRotation = xlCell.Style.Alignment.TextRotation; } catch { }

                    int colSpan = 1, rowSpan = 1;
                    bool isMergedNonMaster = false;
                    if (mergedLookup.TryGetValue((r, c), out var mergedInfo))
                    {
                        if (r == mergedInfo.MasterRow && c == mergedInfo.MasterCol)
                        {
                            colSpan = Math.Min(mergedInfo.LastCol, lastCol) - c + 1;
                            rowSpan = Math.Min(mergedInfo.LastRow, lastRow) - r + 1;
                        }
                        else
                        {
                            isMergedNonMaster = true;
                        }
                    }

                    bool isPinkZone = c >= ColGStart && c <= ColMEnd && r >= RowTagMin && !string.IsNullOrWhiteSpace(text);
                    bool isTagLink = c == ColB && r >= RowTagMin && !string.IsNullOrWhiteSpace(text);

                    cells[r - firstRow, c - firstCol] = new CellInfo(
                        text, xlBgColor, xlFontColor,
                        bold, italic, hAlign, vAlign, textRotation,
                        isPinkZone, isTagLink,
                        colSpan, rowSpan, isMergedNonMaster);
                }
            }

            return new ExtractedData(cells, firstRow, firstCol, totalRows, totalCols);
        }

        private static Dictionary<(int row, int col), MergedCellInfo> BuildMergedCellLookup(
            IXLWorksheet ws, int firstRow, int lastRow, int firstCol, int lastCol)
        {
            var lookup = new Dictionary<(int row, int col), MergedCellInfo>();
            foreach (var range in ws.MergedRanges)
            {
                int rangeFirstRow = Math.Max(range.FirstRow().RowNumber(), firstRow);
                int rangeLastRow = Math.Min(range.LastRow().RowNumber(), lastRow);
                int rangeFirstCol = Math.Max(range.FirstColumn().ColumnNumber(), firstCol);
                int rangeLastCol = Math.Min(range.LastColumn().ColumnNumber(), lastCol);

                if (rangeFirstRow > rangeLastRow || rangeFirstCol > rangeLastCol)
                    continue;

                var info = new MergedCellInfo(
                    range.FirstRow().RowNumber(),
                    range.FirstColumn().ColumnNumber(),
                    range.LastRow().RowNumber(),
                    range.LastColumn().ColumnNumber());

                for (int row = rangeFirstRow; row <= rangeLastRow; row++)
                {
                    for (int col = rangeFirstCol; col <= rangeLastCol; col++)
                        lookup[(row, col)] = info;
                }
            }

            return lookup;
        }

        // ── Construction de la grille DÉTACHÉE du ScrollView ────────────────
        //   Avantage : grid.Add() ne déclenche PAS InvalidateMeasure sur le
        //   ScrollView parent → construction O(N) au lieu de O(N²).
        private async Task<(Grid frozen, Grid scrollable)> BuildGridDetachedAsync(ExtractedData d)
        {
            _cellBorders.Clear();
            _pinkCells.Clear();
            _lastCellClickTime.Clear();

            // Ligne Excel au-delà de laquelle les lignes défilent
            _frozenBoundaryRow = d.FirstRow + Math.Min(FreezeRow - 1, d.TotalRows - 1);
            int frozenRowCount = _frozenBoundaryRow - d.FirstRow + 1; // lignes Excel figées
            int scrollableRowCount = d.TotalRows - frozenRowCount;

            // ── Largeur de chaque colonne (en unités Star) ─────────────────────
            var colWidths = new double[d.TotalCols];
            for (int i = 0; i < d.TotalCols; i++)
            {
                int excelCol = d.FirstCol + i;
                colWidths[i] = excelCol == ColC ? 0.5    // C : moitié
                             : excelCol == ColD ? 0.8    // D : -20 %
                             : (excelCol >= ColGStart && excelCol < ColOExcl) ? 0.425  // G-N : -15% de plus
                             : 1.0;
            }

            // ── Grille figée (lignes 1..FreezeRow) ───────────────────────────
            var frozen = new Grid { HorizontalOptions = LayoutOptions.Fill };
            for (int i = 0; i < frozenRowCount; i++)
            {
                int excelRow = d.FirstRow + i;

                // Lignes Excel 1 à 6 réduites de 50%
                if (excelRow >= 1 && excelRow <= 6)
                {
                    frozen.RowDefinitions.Add(new RowDefinition(RowHeight * 0.5));
                    continue;
                }

                // Ligne Excel 7 (FreezeRow) agrandie pour laisser visibles les en-têtes verticaux G..N
                double h = excelRow == FreezeRow ? RowHeight * 4.25 : RowHeight;
                frozen.RowDefinitions.Add(new RowDefinition(h));
            }
            foreach (var w in colWidths)
                frozen.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w, GridUnitType.Star)));

            // ── Grille scrollable (lignes FreezeRow+1..) ──────────────────────
            var scrollable = new Grid { HorizontalOptions = LayoutOptions.Fill };
            foreach (var w in colWidths)
                scrollable.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w, GridUnitType.Star)));
            for (int i = 0; i < scrollableRowCount; i++)
                scrollable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var colorCache = new Dictionary<int, Color>(32);
            static Color ConvertColor(System.Drawing.Color? source, Color fallback, Dictionary<int, Color> cache)
            {
                if (!source.HasValue)
                    return fallback;

                var value = source.Value;
                var argb = value.ToArgb();
                if (cache.TryGetValue(argb, out var cached))
                    return cached;

                var created = new Color(value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f);
                cache[argb] = created;
                return created;
            }

            var yieldFrequency = d.TotalRows >= 90 ? 4 : 10;

            // ── Cellules de données ───────────────────────────────────────────
            for (int ri = 0; ri < d.TotalRows; ri++)
            {
                if (ri > 0 && ri % yieldFrequency == 0)
                    await Task.Yield();

                int excelRow = d.FirstRow + ri;
                bool isFrozen = excelRow <= _frozenBoundaryRow;
                int gridRow = isFrozen
                    ? ri                    // 0-based dans frozen
                    : (ri - frozenRowCount); // 0-based dans scrollable

                for (int ci = 0; ci < d.TotalCols; ci++)
                {
                    int excelCol = d.FirstCol + ci;
                    var info = d.Cells[ri, ci];

                    if (info.IsMergedNonMaster) continue;

                    Color bgColor = ConvertColor(info.ExcelBgColor, CellWhite, colorCache);

                    Color fontColor = ConvertColor(info.FontColor, Colors.Black, colorCache);

                    if (info.IsTagLink) fontColor = LinkTextColor;

                    bool isFRowToggleCell = excelCol == ColF && excelRow >= RowTagMin && !string.IsNullOrWhiteSpace(info.Text);
                    bool isInteractiveCell = info.IsPinkZone || isFRowToggleCell || info.IsTagLink;

                    var border = MakeDataCell(info.Text, bgColor, fontColor,
                        info.Bold, info.Italic, info.HAlign, info.VAlign,
                        info.TextRotation, info.IsTagLink, excelRow, excelCol, isInteractiveCell);
                    if (isInteractiveCell || info.IsPinkZone)
                        _cellBorders[(excelRow, excelCol)] = border;

                    if (info.IsPinkZone)
                    {
                        int captRow = excelRow, captCol = excelCol;
                        var tap = new TapGestureRecognizer();
                        tap.Tapped += async (_, _) =>
                        {
                            Log($"Tap détecté sur cellule pink-zone ({captRow},{captCol})");
                            await TogglePinkAsync(captRow, captCol);
                        };
                        border.GestureRecognizers.Add(tap);
                    }
                    else if (isFRowToggleCell)
                    {
                        int captRow = excelRow;
                        int captRowSpan = info.RowSpan;
                        var tap = new TapGestureRecognizer();
                        tap.Tapped += async (_, _) =>
                        {
                            Log($"Tap détecté sur cellule F-toggle (ligne {captRow})");
                            await ToggleFNeighborColumnsAsync(captRow, captRowSpan);
                        };
                        border.GestureRecognizers.Add(tap);
                    }
                    else if (info.IsTagLink)
                    {
                        string captText = info.Text;
                        var tap = new TapGestureRecognizer();
                        tap.Tapped += (_, _) => OnTagCellTapped(captText);
                        border.GestureRecognizers.Add(tap);
                    }

                    var target = isFrozen ? frozen : scrollable;
                    Grid.SetColumn(border, ci);
                    Grid.SetRow(border, gridRow);
                    if (info.ColSpan > 1) Grid.SetColumnSpan(border, info.ColSpan);
                    if (info.RowSpan > 1) Grid.SetRowSpan(border, info.RowSpan);
                    target.Add(border);
                }
            }

            return (frozen, scrollable);
        }

        // ── Interactions ──────────────────────────────────────────────────────
        private async Task TogglePinkAsync(int row, int col)
        {
            // Optimisation Android : ignorer les clics multiples rapides
            if (!ShouldProcessCellClick(row, col))
            {
                Log($"TogglePinkAsync({row},{col}) ignoré par debounce");
                return;
            }

            if (_worksheet == null || _workbook == null || _fileInfo == null)
            {
                Log($"TogglePinkAsync({row},{col}) abandonné : worksheet/workbook/fileInfo null");
                return;
            }

            var key = (row, col);
            bool isPink = _pinkCells.Contains(key);

            // Mise à jour UI immédiate pour feedback instantané
            if (_cellBorders.TryGetValue(key, out var border))
            {
                border.BackgroundColor = isPink ? CellWhite : CellPink;

                // Animation rapide de feedback : scale + fade
                _ = border.ScaleToAsync(1.05, 100, Easing.CubicOut);
                _ = border.ScaleToAsync(1.0, 100, Easing.CubicIn);
            }

            // Mise à jour du cache local
            if (isPink)
                _pinkCells.Remove(key);
            else
                _pinkCells.Add(key);

            // Traitement Excel en arrière-plan
            try
            {
                await Task.Run(() =>
                {
                    var cell = _worksheet.Cell(row, col);
                    if (isPink)
                        cell.Style.Fill.BackgroundColor = XLColor.NoColor;
                    else
                        cell.Style.Fill.BackgroundColor = XLColor.Pink;
                });

                MarkWorkbookDirty();
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION dans TogglePinkAsync({row},{col}) : {ex}");
                // Rollback UI en cas d'échec
                if (border != null)
                {
                    border.BackgroundColor = isPink ? CellPink : CellWhite;
                }
                if (isPink)
                    _pinkCells.Add(key);
                else
                    _pinkCells.Remove(key);
            }
        }

        private async Task ToggleFNeighborColumnsAsync(int row, int rowSpan)
        {
            // Optimisation Android : ignorer les clics multiples rapides
            if (!ShouldProcessCellClick(row, ColF))
                return;

            if (_worksheet == null || _workbook == null || _fileInfo == null)
                return;

            var targets = GetFNeighborTargetCells(row, rowSpan);
            if (targets.Count == 0)
                return;

            // Vérifier l'état actuel depuis le cache local
            bool areAllPink = true;
            foreach (var target in targets)
            {
                if (!_pinkCells.Contains((target.row, target.col)))
                {
                    areAllPink = false;
                    break;
                }
            }

            // Sauvegarder l'état avant modification pour rollback potentiel
            var bordersBackup = new List<(Border border, Color color)>();
            var pinkCellsBackup = new HashSet<(int, int)>(_pinkCells);

            // Mise à jour UI immédiate de toutes les cellules concernées
            bool changed = false;
            foreach (var target in targets)
            {
                var key = (target.row, target.col);
                if (_cellBorders.TryGetValue(key, out var border))
                {
                    bordersBackup.Add((border, border.BackgroundColor));
                    if (areAllPink)
                    {
                        border.BackgroundColor = CellWhite;
                        _pinkCells.Remove(key);
                        changed = true;
                    }
                    else
                    {
                        border.BackgroundColor = CellPink;
                        _pinkCells.Add(key);
                        changed = true;
                    }
                }
            }

            if (!changed)
                return;

            // Traitement Excel en arrière-plan
            try
            {
                await Task.Run(() =>
                {
                    foreach (var target in targets)
                    {
                        if (areAllPink)
                            target.cell.Style.Fill.BackgroundColor = XLColor.NoColor;
                        else
                            target.cell.Style.Fill.BackgroundColor = XLColor.Pink;
                    }
                });

                MarkWorkbookDirty();
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION dans ToggleFNeighborColumnsAsync(ligne {row}) : {ex}");
                // Rollback UI en cas d'échec
                foreach (var (border, color) in bordersBackup)
                {
                    border.BackgroundColor = color;
                }
                _pinkCells.Clear();
                foreach (var key in pinkCellsBackup)
                {
                    _pinkCells.Add(key);
                }
            }
        }

        private List<(IXLCell cell, int row, int col)> GetFNeighborTargetCells(int row, int rowSpan)
        {
            var targets = new List<(IXLCell cell, int row, int col)>();
            if (_worksheet == null)
                return targets;

            var uniqueKeys = new HashSet<(int row, int col)>();
            var (firstRow, lastRow) = ResolveFRowRange(row, rowSpan);

            for (int currentRow = firstRow; currentRow <= lastRow; currentRow++)
            {
                for (int col = ColGStart; col <= ColGStart + 2; col++)
                {
                    var target = ResolveMergedMasterCell(currentRow, col);
                    if (GetCellText(target.cell).Length < 1)
                        continue;

                    if (uniqueKeys.Add((target.row, target.col)))
                        targets.Add(target);
                }
            }

            return targets;
        }

        private (IXLCell cell, int row, int col) ResolveMergedMasterCell(int row, int col)
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Worksheet non initialisée.");

            var cell = _worksheet.Cell(row, col);

            try
            {
                var mergedRange = cell.MergedRange();
                if (mergedRange != null)
                {
                    int masterRow = mergedRange.FirstRow().RowNumber();
                    int masterCol = mergedRange.FirstColumn().ColumnNumber();
                    return (_worksheet.Cell(masterRow, masterCol), masterRow, masterCol);
                }
            }
            catch
            {
            }

            return (cell, row, col);
        }

        private bool SetCellPink(IXLCell cell, int row, int col)
        {
            var key = (row, col);
            var fill = cell.Style.Fill;
            var backgroundColor = fill.BackgroundColor.Color;

            bool isWhite = fill.PatternType == XLFillPatternValues.None
                        || backgroundColor.ToArgb() == System.Drawing.Color.White.ToArgb();
            bool isPink = backgroundColor.ToArgb() == System.Drawing.Color.Pink.ToArgb();

            if (!isWhite && !isPink)
                return false;

            if (isPink)
                return false;

            cell.Style.Fill.BackgroundColor = XLColor.Pink;
            _pinkCells.Add(key);

            if (_cellBorders.TryGetValue(key, out var border))
                border.BackgroundColor = CellPink;

            return true;
        }

        private bool ForceCellPink(IXLCell cell, int row, int col)
        {
            if (IsPinkCell(cell))
                return false;

            var key = (row, col);
            cell.Style.Fill.BackgroundColor = XLColor.Pink;
            _pinkCells.Add(key);

            if (_cellBorders.TryGetValue(key, out var border))
                border.BackgroundColor = CellPink;

            return true;
        }

        private bool SetCellWhite(IXLCell cell, int row, int col)
        {
            if (!IsPinkCell(cell))
                return false;

            var key = (row, col);
            cell.Style.Fill.BackgroundColor = XLColor.NoColor;
            _pinkCells.Remove(key);

            if (_cellBorders.TryGetValue(key, out var border))
                border.BackgroundColor = CellWhite;

            return true;
        }

        private bool ToggleWhitePinkCell(IXLCell cell, int row, int col)
        {
            if (SetCellWhite(cell, row, col))
                return true;

            return SetCellPink(cell, row, col);
        }

        private static bool IsPinkCell(IXLCell cell)
        {
            try
            {
                return cell.Style.Fill.BackgroundColor.Color.ToArgb() == System.Drawing.Color.Pink.ToArgb();
            }
            catch
            {
                return false;
            }
        }

        private (int firstRow, int lastRow) ResolveFRowRange(int row, int rowSpan)
        {
            int firstRow = row;
            int lastRow = row + Math.Max(rowSpan, 1) - 1;

            if (_worksheet == null)
                return (firstRow, lastRow);

            try
            {
                var mergedRange = _worksheet.Cell(row, ColF).MergedRange();
                if (mergedRange != null
                    && mergedRange.FirstColumn().ColumnNumber() <= ColF
                    && mergedRange.LastColumn().ColumnNumber() >= ColF)
                {
                    firstRow = mergedRange.FirstRow().RowNumber();
                    lastRow = mergedRange.LastRow().RowNumber();
                }
            }
            catch
            {
            }

            return (firstRow, lastRow);
        }

        private async void OnTagCellTapped(string cellText)
        {
            // Feedback immédiat sur Android : visual cue que le clic a été reçu
            StatusLabel.IsVisible = true;
            StatusLabel.Text = $"Ouverture du fichier {cellText}…";

            try
            {
                if (_fileInfo?.SourceDocumentUri == null && _fileInfo?.Directory == null)
                {
                    await DisplayAlertAsync("Répertoire inconnu",
                        "Impossible de déterminer le répertoire du fichier source. " +
                        "Vérifiez que le fichier est stocké sur le stockage interne du périphérique.",
                        "OK");
                    return;
                }

                string basePath = _fileInfo?.Directory != null
                    ? Path.Combine(_fileInfo.Directory, "fichiers", cellText)
                    : Path.Combine("fichiers", cellText);

                string? accessUri = _fileInfo?.TreeUri ?? _fileInfo?.SourceDocumentUri;
                string[] extensions = [".tag", ".dxf", ".pdf", ".png", ".jpg", ".jpeg"];

                foreach (var extension in extensions)
                {
                    try
                    {
                        await _tagFileOpener.OpenTagFileAsync(basePath + extension, accessUri);
                        return;
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlertAsync("Erreur", $"Impossible d'ouvrir le fichier :\n{ex.Message}", "OK");
                        return;
                    }
                }

                if (_fileInfo?.Directory != null)
                {
                    foreach (var extension in extensions)
                    {
                        var suffixedPath = FindSuffixedFilePath(_fileInfo.Directory, cellText, extension);
                        if (suffixedPath == null)
                            continue;

                        try
                        {
                            await _tagFileOpener.OpenTagFileAsync(suffixedPath, accessUri);
                            return;
                        }
                        catch (FileNotFoundException)
                        {
                        }
                        catch (Exception ex)
                        {
                            await DisplayAlertAsync("Erreur", $"Impossible d'ouvrir le fichier :\n{ex.Message}", "OK");
                            return;
                        }
                    }
                }

                if (_fileInfo?.Directory != null)
                {
                    var autoResolvedPath = FindTagFileInSmbCache(_fileInfo.Directory, cellText, extensions);
                    if (autoResolvedPath != null)
                    {
                        try
                        {
                            await _tagFileOpener.OpenTagFileAsync(autoResolvedPath, accessUri);
                            return;
                        }
                        catch (FileNotFoundException)
                        {
                        }
                        catch (Exception ex)
                        {
                            await DisplayAlertAsync("Erreur", $"Impossible d'ouvrir le fichier :\n{ex.Message}", "OK");
                            return;
                        }
                    }
                }

                await DisplayAlertAsync("Fichier introuvable",
                    $"Aucun fichier correspondant à \"{cellText}\" n'a été trouvé dans le dossier fichiers.",
                    "OK");
            }
            finally
            {
                StatusLabel.IsVisible = false;
            }
        }

        private static string? FindSuffixedFilePath(string baseDirectory, string cellText, string extension)
        {
            var folderPath = Path.Combine(baseDirectory, "fichiers");
            return FindSuffixedFilePathInFolder(folderPath, cellText, extension);
        }

        private static string? FindSuffixedFilePathInFolder(string folderPath, string cellText, string extension)
        {
            if (!Directory.Exists(folderPath))
                return null;

            var prefix = cellText + "-";
            string? selectedFile = null;
            var searchPattern = prefix + "*" + extension;

            foreach (var filePath in Directory.EnumerateFiles(folderPath, searchPattern, SearchOption.TopDirectoryOnly))
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                if (!fileNameWithoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (selectedFile == null ||
                    string.Compare(Path.GetFileName(filePath), Path.GetFileName(selectedFile), StringComparison.OrdinalIgnoreCase) < 0)
                {
                    selectedFile = filePath;
                }
            }

            return selectedFile;
        }

        private static string? FindTagFileInSmbCache(string baseDirectory, string cellText, string[] extensions)
        {
            foreach (var root in EnumerateSmbSearchRoots(baseDirectory))
            {
                foreach (var directory in EnumerateDirectoriesSafe(root))
                {
                    if (!string.Equals(Path.GetFileName(directory), "fichiers", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var extension in extensions)
                    {
                        var exactPath = Path.Combine(directory, cellText + extension);
                        if (File.Exists(exactPath))
                            return exactPath;

                        var suffixedPath = FindSuffixedFilePathInFolder(directory, cellText, extension);
                        if (suffixedPath != null)
                            return suffixedPath;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateSmbSearchRoots(string baseDirectory)
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(baseDirectory) && yielded.Add(baseDirectory))
                yield return baseDirectory;

            var parent = Directory.GetParent(baseDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && yielded.Add(parent))
                yield return parent;

            var marker = $"{Path.DirectorySeparatorChar}.localcache{Path.DirectorySeparatorChar}SMB{Path.DirectorySeparatorChar}";
            int markerIndex = baseDirectory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var smbRoot = baseDirectory.Substring(0, markerIndex + marker.Length);
                if (!string.IsNullOrWhiteSpace(smbRoot) && yielded.Add(smbRoot))
                    yield return smbRoot;
            }
        }

        private static IEnumerable<string> EnumerateDirectoriesSafe(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                yield break;

            var pending = new Stack<string>();
            pending.Push(rootDirectory);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                yield return current;

                string[] childDirectories;
                try
                {
                    childDirectories = Directory.GetDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (var child in childDirectories)
                    pending.Push(child);
            }
        }

        // ── Sauvegarde ────────────────────────────────────────────────────────
        private async Task<string?> SaveWorkbookAsync()
        {
            if (_workbook == null || _fileInfo == null || _worksheet == null)
            {
                Log("SaveWorkbookAsync() abandonné : _workbook/_fileInfo/_worksheet null");
                return null;
            }

            Log($"SaveWorkbookAsync() démarre pour {_fileInfo.FileName} (Directory={_fileInfo.Directory}, SourceDocumentUri={_fileInfo.SourceDocumentUri})");

            await _saveLock.WaitAsync();
            try
            {
                var initialCapacity = _workbookStream is { Length: > 0 and <= int.MaxValue }
                    ? (int)_workbookStream.Length
                    : 256 * 1024;

                await using var workbookBuffer = new MemoryStream(initialCapacity);
                _workbook.SaveAs(workbookBuffer);
                var payload = workbookBuffer.ToArray();
                Log($"Workbook sérialisé en mémoire : {payload.Length} octets");

                var savedPath = await _workbookStorageService.SaveAsync(
                    _fileInfo,
                    stream =>
                    {
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    });

                Log($"_workbookStorageService.SaveAsync() a retourné : '{savedPath}'");

                var replacementStream = new MemoryStream(payload, writable: true);
                var replacementWorkbook = new XLWorkbook(replacementStream);
                var replacementWorksheet = replacementWorkbook.Worksheet(_worksheet.Name);

                var oldWorkbook = _workbook;
                var oldStream = _workbookStream;

                _workbook = replacementWorkbook;
                _worksheet = replacementWorksheet;
                _workbookStream = replacementStream;

                oldWorkbook.Dispose();
                oldStream?.Dispose();

                return savedPath;
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION dans SaveWorkbookAsync() : {ex}");
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    StatusLabel.Text = $"Sauvegarde impossible : {ex.Message}";
                    StatusLabel.IsVisible = true;
                });
                return null;
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private async void OnBackClicked(object sender, EventArgs e)
            => await Shell.Current.GoToAsync("..");

        // ── Helpers visuels ───────────────────────────────────────────────────
        private static Border MakeHeaderCell(string text) => new()
        {
            BackgroundColor = CellHeader,
            StrokeThickness = 1,
            Stroke = CellBorderBrush,
            Padding = new Thickness(4, 0),
            Content = new Label
            {
                Text = text,
                FontAttributes = FontAttributes.Bold,
                FontSize = 12,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalTextAlignment = TextAlignment.Center
            }
        };

        private static Border MakeDataCell(string text, Color bgColor, Color fontColor,
            bool bold, bool italic,
            XLAlignmentHorizontalValues hAlign, XLAlignmentVerticalValues vAlign,
            int textRotation, bool isLink, int excelRow, int excelCol, bool isInteractive)
        {
            bool isCompactHeaderCell = excelRow == FreezeRow
                && excelCol >= ColGStart
                && excelCol <= ColDisplayLast;
            bool isVerticalHeaderCell = isCompactHeaderCell;

            // 255 = texte empilé vertical (stacked), 90 = tourné 90° anti-horaire
            // MAUI Rotation : sens horaire positif → 90° anti-horaire = -90 ou 270
            double rotation = textRotation == 255 ? -90
                            : textRotation > 0 ? -textRotation
                            : 0;

            bool isVertical = rotation != 0;
            bool hasVisibleText = !string.IsNullOrWhiteSpace(text);
            var effectiveFontColor = isCompactHeaderCell ? Colors.Black : fontColor;
            var displayText = isVerticalHeaderCell ? FormatCompactHeaderText(text) : text;
            var effectiveBackgroundColor = isCompactHeaderCell ? CellPink : bgColor;
            var effectiveRotation = isCompactHeaderCell ? 0 : rotation;

            if (!hasVisibleText && !isInteractive && !isVertical)
            {
                return new Border
                {
                    BackgroundColor = effectiveBackgroundColor,
                    StrokeThickness = 1,
                    Stroke = CellBorderBrush,
                    Padding = Thickness.Zero,
                    InputTransparent = true
                };
            }

            var label = new Label
            {
                Text = displayText,
                FontSize = isCompactHeaderCell ? 9 : 12,
                TextColor = effectiveFontColor,
                TextDecorations = isLink ? TextDecorations.Underline : TextDecorations.None,
                FontAttributes = (bold ? FontAttributes.Bold : FontAttributes.None)
                                | (italic ? FontAttributes.Italic : FontAttributes.None),
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                Rotation = effectiveRotation,
                InputTransparent = true,
                LineBreakMode = LineBreakMode.WordWrap,
                MaxLines = isVerticalHeaderCell ? 32 : -1
            };

            return new Border
            {
                BackgroundColor = effectiveBackgroundColor,
                StrokeThickness = 1,
                Stroke = CellBorderBrush,
                Padding = isCompactHeaderCell ? new Thickness(0, 2) : new Thickness(2),
                InputTransparent = !isInteractive && !isCompactHeaderCell,
                Content = label
            };
        }

        private static string FormatCompactHeaderText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var compact = text.Replace(" ", string.Empty)
                              .Replace("/", string.Empty)
                              .Replace("\r", string.Empty)
                              .Replace("\n", string.Empty);

            return string.Join(Environment.NewLine, compact.ToCharArray());
        }

        // ── Helpers utilitaires ───────────────────────────────────────────────
        private static string GetColumnLetter(int colNumber)
        {
            var name = string.Empty;
            while (colNumber > 0)
            {
                int rem = (colNumber - 1) % 26;
                name = (char)('A' + rem) + name;
                colNumber = (colNumber - 1) / 26;
            }
            return name;
        }

        private static string GetCellText(IXLCell cell)
        {
            try { return cell.GetFormattedString(); }
            catch { return cell.Value.ToString() ?? string.Empty; }
        }
    }
}
