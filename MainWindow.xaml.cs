using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    private const int PerformanceIndex = 8;
    private const int CompareIndex = 9;

    private static readonly string LogPath = AppPaths.LogPath;

    private ModelSnapshot _model = new();
    private List<VisualFieldUsage> _visuals = new();
    private List<ObjectUsage> _usage = new();
    private List<PerformanceWarning> _performanceWarnings = new();
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

        SetBusy(true);
        try
        {
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
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Conectar al modelo en vivo y buscar el .pbix en disco pueden tardar
    /// varios segundos; sin esto, la ventana se ve congelada aunque técnicamente
    /// responda (ver revisión de UX). Mientras está ocupada, además, no tiene sentido
    /// dejar exportar — los datos todavía se están cargando.</summary>
    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExportMarkdownButton.IsEnabled = !busy;
        ExportJsonButton.IsEnabled = !busy;
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

            RebuildPerformanceAnalysis();
            ReapplyGridFilter(MeasuresSearchBox, MeasuresGrid);
            ReapplyGridFilter(TablesSearchBox, TablesGrid);
            ReapplyGridFilter(ColumnsSearchBox, ColumnsGrid);
            ReapplyGridFilter(RelationshipsSearchBox, RelationshipsGrid);
            ReapplyGridFilter(PowerQuerySearchBox, PowerQueryGrid);

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
    // Rendimiento — heurísticas de patrones conocidos como antipatrón, basadas
    // solo en metadata (ver PerformanceAnalyzer).
    // =====================================================================

    private void RebuildPerformanceAnalysis()
    {
        if (PerformanceGrid is null) return;

        _performanceWarnings = PerformanceAnalyzer.Analyze(_model);
        PerformanceGrid.ItemsSource = new ObservableCollection<PerformanceWarning>(_performanceWarnings);
        PerformanceEmptyState.Visibility = _performanceWarnings.Count == 0 && _model.Tables.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // =====================================================================
    // Comparar versiones — diff contra un snapshot .json exportado antes
    // (ver ModelDiffAnalyzer / JsonModelExporter).
    // =====================================================================

    private void ChooseSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Snapshot de PBI Context (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var older = JsonModelExporter.Load(dialog.FileName);
            var diff = ModelDiffAnalyzer.Compare(older.Model, _model);

            CompareGrid.ItemsSource = new ObservableCollection<ModelDiffEntry>(diff);
            CompareGrid.Visibility = Visibility.Visible;
            CompareEmptyState.Visibility = Visibility.Collapsed;

            var when = older.ExportedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            StatusText.Text = diff.Count == 0
                ? $"Sin cambios contra el snapshot de {older.SourceName} del {when}."
                : $"{diff.Count} cambios contra el snapshot de {older.SourceName} del {when}.";
        }
        catch (Exception ex)
        {
            Log($"ERROR comparando snapshot: {ex}");
            MessageBox.Show($"No se pudo leer ese snapshot.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =====================================================================
    // Buscador de texto en las grillas — genérico por reflexión: cualquier
    // propiedad string del modelo bindeado cuenta para el filtro, así no hay
    // que mantener una lista de campos "buscables" por tipo a mano.
    // =====================================================================

    private static readonly Dictionary<Type, PropertyInfo[]> SearchablePropsCache = new();

    private static bool MatchesSearch(object item, string search)
    {
        var type = item.GetType();
        if (!SearchablePropsCache.TryGetValue(type, out var props))
        {
            props = type.GetProperties().Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0).ToArray();
            SearchablePropsCache[type] = props;
        }

        foreach (var p in props)
        {
            if (p.GetValue(item) is string value && value.Contains(search, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void GridSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox searchBox) return;
        var grid = searchBox.Name switch
        {
            nameof(MeasuresSearchBox) => MeasuresGrid,
            nameof(TablesSearchBox) => TablesGrid,
            nameof(ColumnsSearchBox) => ColumnsGrid,
            nameof(RelationshipsSearchBox) => RelationshipsGrid,
            nameof(PowerQuerySearchBox) => PowerQueryGrid,
            _ => null
        };
        if (grid is not null) ApplyGridFilter(searchBox, grid);
    }

    private static void ApplyGridFilter(TextBox searchBox, DataGrid grid)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view is null) return;

        var text = searchBox.Text;
        view.Filter = string.IsNullOrWhiteSpace(text) ? null : item => MatchesSearch(item, text);
    }

    /// <summary>El ItemsSource de cada grilla se reemplaza entero al recargar el modelo
    /// (nueva ObservableCollection), lo que resetea cualquier filtro puesto sobre la
    /// vista anterior — esto lo vuelve a aplicar con el texto que ya estaba escrito.</summary>
    private static void ReapplyGridFilter(TextBox searchBox, DataGrid grid) => ApplyGridFilter(searchBox, grid);

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
        if (card.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 10, To = 0, BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>Elevación sutil al pasar el mouse. Antes escalaba la tarjeta entera con un
    /// ScaleTransform — cualquier factor distinto de 1.0 obliga a WPF a resamplear el texto
    /// ya rasterizado (por el DropShadowEffect de la tarjeta), lo que se veía borroso durante
    /// y después del hover. Ahora solo se traslada (sin reescalar nada) y se anima la sombra
    /// propia de la tarjeta — ninguna de las dos cosas fuerza un resampleo del texto.</summary>
    private static void AttachHoverLift(Border card)
    {
        if (card.RenderTransform is not TranslateTransform translate) return;
        var shadow = card.Effect as DropShadowEffect;

        void Animate(double toY, double toBlur, double toOpacity, double toDepth)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(160);
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(toY, duration) { EasingFunction = ease });
            if (shadow is null) return;
            shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(toBlur, duration));
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(toOpacity, duration));
            shadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, new DoubleAnimation(toDepth, duration));
        }

        card.MouseEnter += (_, _) => Animate(-3, 26, 0.12, 6);
        card.MouseLeave += (_, _) => Animate(0, 18, 0.05, 3);
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
            RenderTransform = new TranslateTransform(),
            // Sombra propia (no la del Style "Card", que es una única instancia compartida
            // por todas las tarjetas): así el hover anima solo la tarjeta bajo el mouse.
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.05, BlurRadius = 18, ShadowDepth = 3, Direction = 270 },
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
        SummaryScroll is null || MeasuresPane is null || TablesPane is null || ColumnsPane is null ||
        RelationshipsPane is null || PowerQueryPane is null || UsagePane is null || VisualsPane is null ||
        PerformancePane is null || ComparePane is null
            ? null
            : new UIElement[]
            {
                SummaryScroll, MeasuresPane, TablesPane, ColumnsPane, RelationshipsPane, PowerQueryPane,
                UsagePane, VisualsPane, PerformancePane, ComparePane
            };

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
        else if (index == PerformanceIndex)
        {
            StatusText.Text = _performanceWarnings.Count == 0
                ? "Heurísticas sobre metadata — sin patrones de riesgo detectados."
                : $"{_performanceWarnings.Count} alertas — heurísticas sobre metadata, no un profiler real.";
        }
        else if (index == CompareIndex)
        {
            StatusText.Text = "Elegí un snapshot .json exportado antes para ver qué cambió.";
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

    private void ExportMarkdownButton_Click(object sender, RoutedEventArgs e)
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

    private void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Tables.Count == 0 && _model.Measures.Count == 0 && _visuals.Count == 0)
        {
            MessageBox.Show("No hay nada cargado todavía — conectate a un modelo o abrí un .pbix primero.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"Snapshot_{_sourceName}.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            JsonModelExporter.Export(dialog.FileName, _sourceName, _model, _visuals, _usage);
            MessageBox.Show(
                "Listo. Guardá este archivo — más adelante podés usarlo en \"Comparar versiones\" para ver qué cambió.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"ERROR generando JSON: {ex}");
            MessageBox.Show($"No se pudo generar el archivo.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
