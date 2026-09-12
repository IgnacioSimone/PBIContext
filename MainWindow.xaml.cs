using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using PBIExplorer.Models;
using PBIExplorer.Services;

namespace PBIExplorer;

public partial class MainWindow : Window
{
    private const string AppName = "PBI Context";
    private const int SummaryIndex = 0;
    private const int PowerQueryIndex = 5;
    private const int UsageIndex = 6;
    private const int VisualsIndex = 7;

    private static readonly string LogPath = AppPaths.LogPath;

    private ModelSnapshot _model = new();
    private List<VisualFieldUsage> _visuals = new();
    private List<ObjectUsage> _usage = new();
    private string _sourceName = "modelo";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n"); }
        catch { /* el log es best-effort, nunca debe romper la app */ }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        Log($"Inicio. Args: {string.Join(" | ", args)}");

        // Power BI Desktop invoca la herramienta externa como:
        //   PBIExplorer.exe "localhost:PUERTO" "GUID-de-la-base"
        if (args.Length >= 3)
        {
            await ConnectToLiveModelAsync(args[1], args[2]);
        }

        // El primer SelectionChanged del ListBox ocurre durante InitializeComponent,
        // cuando los paneles todavía no existen; hay que aplicar el estado una vez acá.
        ApplyNavState();
        RebuildSummary();

        await TryAutoLoadVisualsAsync();
    }

    // =====================================================================
    // Modelo en vivo (metadata vía DMVs)
    // =====================================================================

    private async Task ConnectToLiveModelAsync(string server, string database)
    {
        try
        {
            StatusText.Text = $"Conectando a {server} …";

            // El modelo puede tener miles de medidas/columnas: leerlo en el hilo de UI
            // congelaría la ventana (y hasta la animación de entrada) mientras dura la
            // conexión. Solo la lectura pesada va a un hilo de fondo; el resto sigue en
            // el hilo de UI de siempre.
            var reader = new AdomdModelReader(server, database);
            _model = await Task.Run(() => reader.ReadAll());
            _sourceName = database;

            TablesGrid.ItemsSource = new ObservableCollection<TableInfo>(_model.Tables);
            ColumnsGrid.ItemsSource = new ObservableCollection<ColumnInfo>(_model.Columns);
            MeasuresGrid.ItemsSource = new ObservableCollection<MeasureInfo>(_model.Measures);
            RelationshipsGrid.ItemsSource = new ObservableCollection<RelationshipInfo>(_model.Relationships);
            PowerQueryGrid.ItemsSource = new ObservableCollection<PowerQueryInfo>(_model.PowerQueries);

            Log($"Modelo leído: {_model.Tables.Count} tablas, {_model.Columns.Count} columnas, " +
                $"{_model.Measures.Count} medidas, {_model.Relationships.Count} relaciones, " +
                $"{_model.PowerQueries.Count} consultas Power Query, {_model.Warnings.Count} advertencias.");

            UpdateStatusForModel();
            ApplyNavState();
            RebuildUsageAnalysis();
            RebuildSummary();

            if (_model.Warnings.Count > 0)
            {
                MessageBox.Show(
                    "Algunas secciones no se pudieron leer del motor de este modelo:\n\n" +
                    string.Join("\n\n", _model.Warnings),
                    $"{AppName} — advertencias", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR conectando al modelo: {ex}");
            StatusText.Text = "No se pudo conectar al modelo en vivo.";
            MessageBox.Show($"No se pudo conectar al modelo de Power BI Desktop.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateStatusForModel()
    {
        if (_model.Tables.Count == 0 && _model.Measures.Count == 0) return;

        StatusText.Text =
            $"{_model.Tables.Count} tablas · {_model.Columns.Count} columnas · " +
            $"{_model.Measures.Count} medidas · {_model.Relationships.Count} relaciones";
    }

    // =====================================================================
    // Visuales del reporte (leídos del archivo .pbix)
    // =====================================================================

    /// <summary>
    /// Intenta encontrar y leer solo el .pbix abierto en Power BI Desktop, para que
    /// la pestaña de visuales se llene sola igual que el resto.
    /// </summary>
    private async Task TryAutoLoadVisualsAsync()
    {
        var reportName = PbixLocator.GetOpenReportName();
        Log($"Reporte abierto según título de ventana: {reportName ?? "(ninguno)"}");

        if (reportName is null)
        {
            ShowVisualsEmptyState(
                "No se detectó ningún tablero abierto",
                "Abrí un tablero en Power BI Desktop y volvé a lanzar la herramienta, o elegí el archivo a mano.");
            return;
        }

        ShowVisualsEmptyState($"Buscando \"{reportName}.pbix\" en el disco…",
            "Los visuales y sus campos viven dentro del archivo, no en el modelo en memoria.");

        var path = await Task.Run(() => PbixLocator.FindOpenPbix(TimeSpan.FromSeconds(12)));
        Log($"Ruta detectada: {path ?? "(no encontrada)"}");

        if (path is null)
        {
            ShowVisualsEmptyState(
                $"No encontré \"{reportName}.pbix\" automáticamente",
                "Puede estar en una carpeta fuera de la búsqueda. Elegilo a mano y se carga igual.");
            return;
        }

        LoadVisualsFrom(path);
    }

    private void LoadVisualsFrom(string path)
    {
        try
        {
            Log($"Leyendo visuales de: {path}");
            _visuals = new PbixReportParser().ParseVisuals(path);
            Log($"ParseVisuals devolvió {_visuals.Count} filas.");

            VisualsGrid.ItemsSource = new ObservableCollection<VisualFieldUsage>(_visuals);

            if (_model.Tables.Count == 0) _sourceName = Path.GetFileNameWithoutExtension(path);

            if (_visuals.Count == 0)
            {
                ShowVisualsEmptyState("Ese archivo no tiene visuales legibles",
                    "Se probaron los dos formatos de reporte (PBIR y Layout clásico) y ninguno devolvió visuales.");
                RebuildUsageAnalysis();
                RebuildSummary();
                return;
            }

            VisualsEmptyState.Visibility = Visibility.Collapsed;

            var pages = _visuals.Select(v => v.Page).Distinct().Count();
            StatusText.Text = $"{Path.GetFileName(path)} · {pages} páginas · {_visuals.Count} usos de campos";
            RebuildUsageAnalysis();
            RebuildSummary();
        }
        catch (Exception ex)
        {
            Log($"ERROR leyendo visuales: {ex}");
            ShowVisualsEmptyState("No se pudo leer ese archivo", ex.Message);
        }
    }

    private void ShowVisualsEmptyState(string title, string body)
    {
        VisualsEmptyTitle.Text = title;
        VisualsEmptyBody.Text = body;
        VisualsEmptyState.Visibility = Visibility.Visible;
    }

    private void ChoosePbixButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Archivos Power BI (*.pbix)|*.pbix" };
        if (dialog.ShowDialog() != true) return;

        LoadVisualsFrom(dialog.FileName);
        NavList.SelectedIndex = VisualsIndex;
    }

    // =====================================================================
    // Uso y limpieza — cruza visuales + dependencias DAX + relaciones para
    // detectar medidas/columnas/tablas candidatas a eliminar.
    // =====================================================================

    private void RebuildUsageAnalysis()
    {
        if (UsageGrid is null) return;

        _usage = UsageAnalyzer.Analyze(_model, _visuals);
        ApplyUsageFilter();
    }

    private void UsageFilter_Changed(object sender, RoutedEventArgs e) => ApplyUsageFilter();

    private void ApplyUsageFilter()
    {
        if (UsageGrid is null || UsageOnlyUnusedCheck is null) return;

        var onlyUnused = UsageOnlyUnusedCheck.IsChecked == true;
        var shown = onlyUnused ? _usage.Where(u => u.IsUnused).ToList() : _usage;
        UsageGrid.ItemsSource = new ObservableCollection<ObjectUsage>(shown);
    }

    // =====================================================================
    // Resumen — análisis heurístico local, sin IA, de qué muestra el tablero
    // =====================================================================

    private void RebuildSummary()
    {
        if (SummaryStack is null) return;

        SummaryStack.Children.Clear();

        var hasAnything = _model.Tables.Count > 0 || _model.Measures.Count > 0 || _visuals.Count > 0;
        if (!hasAnything)
        {
            SummaryStack.Children.Add(BuildCard(stack =>
            {
                stack.Children.Add(NewText(
                    "Conectate a un modelo desde Power BI Desktop y/o abrí el .pbix para ver los números acá.",
                    13, FontWeights.Normal, (Brush)FindResource("InkDim")));
            }));
            return;
        }

        var pages = _visuals.Select(v => v.Page).Distinct().Count();
        var visualCount = _visuals.Select(v => (v.Page, v.VisualType, v.Title)).Distinct().Count();
        var hiddenTables = _model.Tables.Count(t => t.IsHidden);
        var hiddenMeasures = _model.Measures.Count(m => m.IsHidden);

        var tiles = new (string Icon, string Label, int Value, string? Sub)[]
        {
            ("", "Tablas",         _model.Tables.Count,        hiddenTables   > 0 ? $"{hiddenTables} ocultas"   : null),
            ("", "Columnas",       _model.Columns.Count,       null),
            ("", "Medidas DAX",    _model.Measures.Count,      hiddenMeasures > 0 ? $"{hiddenMeasures} ocultas" : null),
            ("", "Relaciones",     _model.Relationships.Count, null),
            ("", "Power Query",    _model.PowerQueries.Count,  null),
            ("", "Páginas",        pages,                      null),
            ("", "Visuales",       visualCount,                null),
            ("", "Usos de campos", _visuals.Count,             null),
            ("", "Sin uso",         _usage.Count(u => u.IsUnused), null),
        };

        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        var shown = 0;
        foreach (var tile in tiles)
        {
            if (tile.Value == 0 && !hasAnything) continue;
            var card = BuildTile(tile.Icon, tile.Label, tile.Value, tile.Sub);
            grid.Children.Add(card);
            AnimateTileIn(card, shown++);
        }
        SummaryStack.Children.Add(grid);
    }

    /// <summary>Entrada escalonada, tipo macOS: cada tarjeta aparece un poco después que
    /// la anterior — se nota fluido sin depender de nada más que WPF nativo (liviano).</summary>
    private static void AnimateTileIn(UIElement card, int order)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(order, 10) * 35);
        card.Opacity = 0;
        card.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1, BeginTime = delay,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        if (card.RenderTransform is TransformGroup group && group.Children[1] is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 10, To = 0, BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>Elevación sutil al pasar el mouse — misma curva que ya usan los botones,
    /// nada de bitmaps ni blur pesado: dos DoubleAnimation sobre un ScaleTransform.</summary>
    private static void AttachHoverLift(Border card)
    {
        if (card.RenderTransform is not TransformGroup group || group.Children[0] is not ScaleTransform scale) return;

        void Animate(double to) => AnimateScale(scale, to);

        card.MouseEnter += (_, _) => Animate(1.025);
        card.MouseLeave += (_, _) => Animate(1.0);
    }

    private static void AnimateScale(ScaleTransform scale, double to)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { EasingFunction = ease });
    }

    private Border BuildTile(string icon, string label, int value, string? sub)
    {
        var badge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("AccentSoft"),
            Margin = new Thickness(0, 0, 0, 16),
            Child = new TextBlock
            {
                Text = icon,
                FontFamily = (FontFamily)FindResource("FontIcon"),
                FontSize = 17,
                Foreground = (Brush)FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var stack = new StackPanel();
        stack.Children.Add(badge);
        stack.Children.Add(NewText(value.ToString("N0"), 32, FontWeights.Bold, (Brush)FindResource("Ink")));
        stack.Children.Add(NewText(label, 12.5, FontWeights.SemiBold, (Brush)FindResource("InkDim"), margin: new Thickness(0, 3, 0, 0)));
        if (sub is not null)
            stack.Children.Add(NewText(sub, 11, FontWeights.Normal, (Brush)FindResource("InkFaint"), margin: new Thickness(0, 2, 0, 0)));

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Width = 196,
            MinHeight = 152,
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 14, 14),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(), new TranslateTransform() }
            },
            Child = stack
        };
        AttachHoverLift(card);
        return card;
    }

    private Border BuildCard(Action<StackPanel> fill)
    {
        var stack = new StackPanel();
        fill(stack);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 14),
            Child = stack
        };
    }

    private static TextBlock NewText(string text, double size, FontWeight weight, Brush foreground, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        FontFamily = (FontFamily)Application.Current.FindResource("FontText"),
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0)
    };

    // =====================================================================
    // Navegación
    // =====================================================================

    /// <summary>
    /// Null hasta que el XAML termina de construirse. El ListBox trae SelectedIndex=0,
    /// así que este evento dispara en pleno InitializeComponent, cuando los paneles
    /// todavía no existen.
    /// </summary>
    private UIElement[]? Panes =>
        SummaryScroll is null || MeasuresGrid is null || TablesGrid is null || ColumnsGrid is null ||
        RelationshipsGrid is null || PowerQueryGrid is null || UsagePane is null || VisualsPane is null
            ? null
            : new UIElement[] { SummaryScroll, MeasuresGrid, TablesGrid, ColumnsGrid, RelationshipsGrid, PowerQueryGrid, UsagePane, VisualsPane };

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyNavState();

    private void ApplyNavState()
    {
        var index = NavList?.SelectedIndex ?? -1;
        if (index < 0) return;

        var panes = Panes;
        if (panes is null) return;
        for (var i = 0; i < panes.Length; i++)
            panes[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        if (SectionTitle is null || StatusText is null || ModelEmptyState is null ||
            ModelEmptyTitle is null || ModelEmptyBody is null) return;

        if (NavList!.SelectedItem is ListBoxItem item)
            SectionTitle.Text = item.Content?.ToString() ?? "";

        // Resumen y Visuales tienen sus propios estados vacíos; las 5 secciones del
        // modelo (Medidas/Tablas/Columnas/Relaciones/Power Query) comparten este uno,
        // pero con título y cuerpo adaptados: "no hay conexión" vs. "esta sección
        // puntual no tiene filas" (ej. un modelo real con cero relaciones definidas).
        var isModelSection = index is > SummaryIndex and < VisualsIndex;
        var notConnected = _model.Tables.Count == 0 && _model.Measures.Count == 0;
        var sectionIsEmpty = index switch
        {
            1 => _model.Measures.Count == 0,
            2 => _model.Tables.Count == 0,
            3 => _model.Columns.Count == 0,
            4 => _model.Relationships.Count == 0,
            PowerQueryIndex => _model.PowerQueries.Count == 0,
            UsageIndex => _model.Measures.Count == 0 && _model.Columns.Count == 0,
            _ => false
        };

        if (isModelSection && notConnected)
        {
            ModelEmptyTitle.Text = "Sin modelo conectado";
            ModelEmptyBody.Text = "Abrí tu tablero en Power BI Desktop y lanzá PBI Context desde la pestaña Herramientas externas. Ahí se llenan medidas, tablas, columnas y relaciones.";
            ModelEmptyState.Visibility = Visibility.Visible;
        }
        else if (isModelSection && sectionIsEmpty)
        {
            ModelEmptyTitle.Text = "Esta sección está vacía";
            ModelEmptyBody.Text = index switch
            {
                1 => "El modelo conectado no tiene medidas DAX.",
                2 => "El modelo conectado no tiene tablas visibles.",
                3 => "El modelo conectado no tiene columnas.",
                4 => "El modelo conectado no tiene relaciones definidas.",
                UsageIndex => "No hay medidas ni columnas para analizar todavía.",
                _ => "El modelo conectado no tiene consultas de Power Query."
            };
            ModelEmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            ModelEmptyState.Visibility = Visibility.Collapsed;
        }

        if (index == VisualsIndex && _visuals.Count > 0)
        {
            var pages = _visuals.Select(v => v.Page).Distinct().Count();
            StatusText.Text = $"{pages} páginas · {_visuals.Count} usos de campos";
        }
        else if (index == SummaryIndex)
        {
            StatusText.Text = "Análisis local, basado en reglas — no usa IA ni sale a internet.";
        }
        else if (index == UsageIndex && _usage.Count > 0)
        {
            var unused = _usage.Count(u => u.IsUnused);
            StatusText.Text = $"{unused} de {_usage.Count} objetos sin ningún uso detectado — cruza visuales, DAX y relaciones.";
        }
        else if (isModelSection)
        {
            UpdateStatusForModel();
        }

        AnimateContentIn(panes[index]);
    }

    /// <summary>Fundido suave al cambiar de sección, como una transición de macOS.</summary>
    private static void AnimateContentIn(UIElement pane)
    {
        pane.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // =====================================================================
    // Exportación
    // =====================================================================

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (NavList.SelectedIndex == SummaryIndex)
        {
            MessageBox.Show("El resumen es texto libre, no una tabla — incluilo con \"Exportar a Word\".",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (name, headers, rows) = NavList.SelectedIndex switch
        {
            1 => ("Medidas",
                new[] { "Tabla", "Medida", "Carpeta", "DAX", "Formato", "Oculta", "Descripción" },
                _model.Measures.Select(m => new object?[]
                    { m.Table, m.Name, m.DisplayFolder, SensitiveTextGuard.Mask(m.Expression), m.FormatString, m.IsHidden, SensitiveTextGuard.Mask(m.Description) })),

            2 => ("Tablas",
                new[] { "Tabla", "Columnas", "Medidas", "Descripción", "Oculta" },
                _model.Tables.Select(t => new object?[]
                    { t.Name, t.ColumnCount, t.MeasureCount, SensitiveTextGuard.Mask(t.Description), t.IsHidden })),

            3 => ("Columnas",
                new[] { "Tabla", "Columna", "Tipo de dato", "Origen", "Carpeta", "DAX / Columna origen", "Oculta" },
                _model.Columns.Select(c => new object?[]
                    { c.Table, c.Name, c.DataType, c.ColumnType, c.DisplayFolder, SensitiveTextGuard.Mask(c.DaxOrSource), c.IsHidden })),

            4 => ("Relaciones",
                new[] { "Desde", "Columna", "Hacia", "Columna", "Cardinalidad", "Filtro cruzado", "Activa" },
                _model.Relationships.Select(r => new object?[]
                    { r.FromTable, r.FromColumn, r.ToTable, r.ToColumn, r.Cardinality, r.CrossFilterBehavior, r.IsActive })),

            PowerQueryIndex => ("PowerQuery",
                new[] { "Nombre", "Tipo", "Código M", "Descripción" },
                _model.PowerQueries.Select(p => new object?[]
                    { p.Name, p.Kind, SensitiveTextGuard.Mask(p.MCode), SensitiveTextGuard.Mask(p.Description) })),

            UsageIndex => ("UsoYLimpieza",
                new[] { "Tipo", "Tabla", "Nombre", "Estado", "Depende de", "Usado por" },
                _usage.Select(u => new object?[]
                    { u.Kind, u.Table, u.Name, u.Status, u.DependsOnText, u.UsedByText })),

            VisualsIndex => ("Visuales",
                new[] { "Página", "Visual", "Título", "Tipo", "Tabla", "Campo" },
                _visuals.Select(v => new object?[]
                    { v.Page, v.VisualType, SensitiveTextGuard.Mask(v.Title), v.FieldKind, v.Table, v.Field })),

            _ => (null, Array.Empty<string>(), Enumerable.Empty<object?[]>())
        };

        if (name is null || !rows.Any())
        {
            MessageBox.Show("No hay datos cargados en esta sección todavía.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"{name}_{_sourceName}.csv" };
        if (dialog.ShowDialog() != true) return;

        CsvExporter.Export(dialog.FileName, headers, rows);
        MessageBox.Show("Exportado correctamente.", AppName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportWordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Tables.Count == 0 && _model.Measures.Count == 0 && _visuals.Count == 0)
        {
            MessageBox.Show("No hay nada cargado todavía — conectate a un modelo o abrí un .pbix primero.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog { Filter = "Word (*.docx)|*.docx", FileName = $"Documentacion_{_sourceName}.docx" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var summary = SummaryBuilder.Build(_model, _visuals);
            DocxExporter.ExportFullDocumentation(dialog.FileName, _sourceName,
                _model.Tables, _model.Columns, _model.Measures, _model.Relationships, _model.PowerQueries, _visuals, summary, _usage);
            MessageBox.Show("Documento generado correctamente.", AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"ERROR generando Word: {ex}");
            MessageBox.Show($"No se pudo generar el documento.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportAiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Tables.Count == 0 && _model.Measures.Count == 0 && _visuals.Count == 0)
        {
            MessageBox.Show("No hay nada cargado todavía — conectate a un modelo o abrí un .pbix primero.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = $"Contexto_IA_{_sourceName}.md" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var summary = SummaryBuilder.Build(_model, _visuals);
            AiContextExporter.Export(dialog.FileName, _sourceName, _model, _visuals, summary, _usage);
            MessageBox.Show(
                "Listo. Adjuntá o pegá ese .md en el chat con Claude o ChatGPT para que analice todo el tablero.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"ERROR generando contexto IA: {ex}");
            MessageBox.Show($"No se pudo generar el archivo.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
