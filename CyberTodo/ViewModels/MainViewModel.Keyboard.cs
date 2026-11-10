using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    // ───────── Klavyeyle liste yönetimi ─────────

    /// <summary>Ok tuşlarıyla seçilen aktif görev (görünür listede).</summary>
    [ObservableProperty]
    private CyberTask? _selectedTask;

    partial void OnSelectedTaskChanged(CyberTask? oldValue, CyberTask? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    private List<CyberTask> VisibleActiveTasks() => [.. ActiveView.Cast<CyberTask>()];

    /// <summary>↑/↓: seçimi görünür sırada kaydırır. Seçim yoksa ilk (↓) ya da son (↑) satır seçilir.</summary>
    public void MoveSelection(int delta)
    {
        var visible = VisibleActiveTasks();
        if (visible.Count == 0)
        {
            SelectedTask = null;
            return;
        }

        int index = SelectedTask is null ? -1 : visible.IndexOf(SelectedTask);
        int next = index < 0 ? (delta >= 0 ? 0 : visible.Count - 1) : Math.Clamp(index + delta, 0, visible.Count - 1);
        SelectedTask = visible[next];
    }

    /// <summary>Home / End.</summary>
    public void SelectEdge(bool last)
    {
        var visible = VisibleActiveTasks();
        SelectedTask = visible.Count == 0 ? null : (last ? visible[^1] : visible[0]);
    }

    /// <summary>Space: seçili görevi tamamlar; seçim aynı sıradaki (bir sonraki) satıra geçer.</summary>
    public async Task CompleteSelectedAsync()
    {
        if (SelectedTask is not { } task)
        {
            return;
        }

        int index = VisibleActiveTasks().IndexOf(task);
        await CompleteTaskCommand.ExecuteAsync(task);
        if (task.IsCompleted)
        {
            SelectNearIndex(index);
        }
    }

    /// <summary>Delete: seçili görevi siler (GERİ AL ile geri gelir); seçim komşu satıra geçer.</summary>
    public async Task DeleteSelectedAsync()
    {
        if (SelectedTask is not { } task)
        {
            return;
        }

        int index = VisibleActiveTasks().IndexOf(task);
        await DeleteTaskCommand.ExecuteAsync(task);
        SelectNearIndex(index);
    }

    public void EditSelected()
    {
        if (SelectedTask is { } task)
        {
            EditTaskCommand.Execute(task);
        }
    }

    public Task ToggleFocusSelectedAsync() =>
        SelectedTask is { } task ? StartFocusCommand.ExecuteAsync(task) : Task.CompletedTask;

    private void SelectNearIndex(int index)
    {
        var visible = VisibleActiveTasks();
        SelectedTask = visible.Count == 0 ? null : visible[Math.Clamp(index, 0, visible.Count - 1)];
    }
}
