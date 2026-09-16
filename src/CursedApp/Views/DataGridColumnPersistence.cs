using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CursedApp.Views;

/// <summary>
/// Remembers the widths the user drags columns to, and restores them next run.
///
/// It listens to <see cref="DataGridColumn.WidthProperty"/> rather than
/// ActualWidth: a proportional column keeps its star Width while its ActualWidth
/// changes on every window resize, so watching ActualWidth would quietly freeze
/// every column at whatever pixel size the first layout produced. Dragging a
/// column, by contrast, replaces Width with an absolute value — which is exactly
/// the signal worth persisting.
/// </summary>
internal sealed class DataGridColumnPersistence
{
    private readonly DataGrid _grid;
    private readonly string _gridKey;
    private readonly IDictionary<string, double> _store;
    private readonly Action _save;
    private readonly DispatcherTimer _debounce;
    private readonly List<(DataGridColumn Column, DependencyPropertyDescriptor Descriptor, EventHandler Handler)> _subscriptions = [];

    private bool _restoring;

    private DataGridColumnPersistence(
        DataGrid grid,
        string gridKey,
        IDictionary<string, double> store,
        Action save)
    {
        _grid = grid;
        _gridKey = gridKey;
        _store = store;
        _save = save;

        // Dragging a splitter produces a stream of width changes; write once at rest.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _save();
        };
    }

    /// <summary>
    /// Restores saved widths for <paramref name="grid"/> and keeps them in sync.
    /// </summary>
    public static DataGridColumnPersistence Attach(
        DataGrid grid,
        string gridKey,
        IDictionary<string, double> store,
        Action save)
    {
        var persistence = new DataGridColumnPersistence(grid, gridKey, store, save);
        persistence.Restore();
        persistence.Subscribe();

        grid.Unloaded += (_, _) => persistence.Unsubscribe();
        grid.Loaded += (_, _) => persistence.Subscribe();

        return persistence;
    }

    private string KeyFor(DataGridColumn column)
    {
        var header = column.Header as string;
        return string.IsNullOrWhiteSpace(header)
            ? $"{_gridKey}:#{_grid.Columns.IndexOf(column)}"
            : $"{_gridKey}:{header}";
    }

    private void Restore()
    {
        _restoring = true;
        try
        {
            foreach (var column in _grid.Columns)
            {
                if (_store.TryGetValue(KeyFor(column), out var width) && width > 0)
                    column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void Subscribe()
    {
        Unsubscribe();

        foreach (var column in _grid.Columns)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(
                DataGridColumn.WidthProperty, typeof(DataGridColumn));

            if (descriptor is null)
                continue;

            var captured = column;
            void Handler(object? sender, EventArgs e) => OnWidthChanged(captured);

            descriptor.AddValueChanged(column, Handler);
            _subscriptions.Add((column, descriptor, Handler));
        }
    }

    private void Unsubscribe()
    {
        foreach (var (column, descriptor, handler) in _subscriptions)
            descriptor.RemoveValueChanged(column, handler);

        _subscriptions.Clear();
    }

    private void OnWidthChanged(DataGridColumn column)
    {
        if (_restoring)
            return;

        var width = column.Width;

        // Only a user drag turns a column absolute. Anything still proportional
        // is the layout doing its job, and must not be pinned down.
        if (!width.IsAbsolute || width.DisplayValue <= 0)
            return;

        _store[KeyFor(column)] = Math.Round(width.DisplayValue, 1);

        _debounce.Stop();
        _debounce.Start();
    }
}
