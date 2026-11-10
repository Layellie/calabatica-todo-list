using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    // ───────── Şablonlar ─────────

    public ObservableCollection<TaskTemplate> Templates { get; } = [];

    [ObservableProperty]
    private bool _isTemplatesOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateTemplateCommand))]
    private string _newTemplateName = string.Empty;

    [RelayCommand]
    private void OpenTemplates()
    {
        CloseOverlays();
        IsTemplatesOpen = true;
    }

    [RelayCommand]
    private void CloseTemplates() => IsTemplatesOpen = false;

    private bool CanCreateTemplate() => !string.IsNullOrWhiteSpace(NewTemplateName);

    /// <summary>Görünen (filtrelenmiş) aktif görevlerden şablon. Tarihler bugüne göre gün farkı olarak saklanır.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateTemplate))]
    private async Task CreateTemplateAsync()
    {
        var visible = ActiveView.Cast<CyberTask>().ToList();
        string name = NewTemplateName.Trim();
        if (visible.Count == 0 || name.Length == 0)
        {
            return;
        }

        var today = Today;
        var template = new TaskTemplate
        {
            Name = name.Length > 60 ? name[..60] : name,
            Items =
            [
                .. visible.Select(t => new TemplateItem
                {
                    Title = t.Title,
                    Category = t.Category,
                    Notes = t.Notes,
                    Priority = t.Priority,
                    EstimatedHours = t.EstimatedHours,
                    Recurrence = t.Recurrence,
                    RepeatDays = t.RepeatDays,
                    DueDayOffset = t.DueAt is { } due ? Math.Max(0, DateOnly.FromDateTime(due).DayNumber - today.DayNumber) : null,
                    DueTime = t.DueAt is { } dueTime && t.HasDueTime ? TimeOnly.FromDateTime(dueTime) : null,
                    ReminderMinutesBefore = t.ReminderMinutesBefore,
                    Subtasks = [.. t.Subtasks.Select(static s => s.Title)],
                }),
            ],
        };

        Templates.Add(template);
        NewTemplateName = string.Empty;
        ShowToast(_loc.Format("Toast_TemplateSaved", template.Name), ToastKind.Info, lifetime: s_infoLifetime);
        await SaveAsync();
    }

    /// <summary>Şablondaki görevleri yeni görev olarak ekler (yeni numaralar, tarihler bugüne göre).</summary>
    [RelayCommand]
    private async Task ApplyTemplateAsync(TaskTemplate? template)
    {
        if (template is null || template.Items.Count == 0)
        {
            return;
        }

        var now = Now;
        var today = Today;
        var added = new List<CyberTask>(template.Items.Count);
        foreach (var item in template.Items)
        {
            var dueDate = item.DueDayOffset is { } offset ? today.AddDays(offset) : (DateOnly?)null;
            if (item.Recurrence == Recurrence.SelectedDays && dueDate is { } d)
            {
                dueDate = RecurrenceRules.FirstMatchOnOrAfter(d, item.RepeatDays);
            }

            var task = new CyberTask
            {
                Number = _nextTaskNumber++,
                Title = item.Title,
                Category = item.Category,
                Notes = item.Notes,
                Priority = item.Priority,
                EstimatedHours = item.EstimatedHours,
                Recurrence = item.Recurrence,
                RepeatDays = item.RepeatDays,
                DueAt = dueDate?.ToDateTime(item.DueTime ?? TimeOnly.MinValue),
                HasDueTime = dueDate is not null && item.DueTime is not null,
                ReminderMinutesBefore = dueDate is null ? null : item.ReminderMinutesBefore,
                Subtasks = [.. item.Subtasks.Select(static s => new SubTask { Title = s })],
                CreatedAt = now,
                SortOrder = NextSortOrder(),
            };

            UpdateDerivedState(task, now);
            ActiveTasks.Add(task);
            added.Add(task);
        }

        IsTemplatesOpen = false;
        OnTasksChanged(TaskChange.Membership);
        PushUndo(_loc.Format("Undo_Template", template.Name), async () =>
        {
            foreach (var task in added)
            {
                if (Focus.Task == task)
                {
                    StopFocusCore(Now);
                }

                ActiveTasks.Remove(task); // tamamlanmış/silinmiş olanlar zaten listede yok
            }

            OnTasksChanged(TaskChange.Membership);
            await SaveAsync();
        });
        ShowToast(_loc.Format("Toast_TemplateApplied", template.Name, template.Items.Count), ToastKind.Info, lifetime: s_infoLifetime);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(TaskTemplate? template)
    {
        if (template is not null && Templates.Remove(template))
        {
            await SaveAsync();
        }
    }
}
