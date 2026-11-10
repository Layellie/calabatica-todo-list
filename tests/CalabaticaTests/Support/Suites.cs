namespace CalabaticaTests;

/// <summary>Test paketleri ve çalışma sırası. Sıra önemli: LoggingTests, önceki paketlerin ürettiği hata kayıtlarını kontrol eder.</summary>
internal static class Suites
{
    public static IReadOnlyList<(string Name, Func<TestContext, Task> Run)> All { get; } =
    [
        (nameof(CoreScenarioTests), CoreScenarioTests.Run),
        (nameof(UndoTests), UndoTests.Run),
        (nameof(FocusStatsTests), FocusStatsTests.Run),
        (nameof(BackupTests), BackupTests.Run),
        (nameof(ExportTests), ExportTests.Run),
        (nameof(UpdateInstallerTests), UpdateInstallerTests.Run),
        (nameof(ReminderAndBreakTests), ReminderAndBreakTests.Run),
        (nameof(HabitTests), HabitTests.Run),
        (nameof(ListInteractionTests), ListInteractionTests.Run),
        (nameof(ViewsAndOnboardingTests), ViewsAndOnboardingTests.Run),
        (nameof(DataAndPlatformTests), DataAndPlatformTests.Run),
        (nameof(ExtractedClassesTests), ExtractedClassesTests.Run),
        (nameof(LoggingTests), LoggingTests.Run),
        (nameof(UiSmokeTests), UiSmokeTests.Run),
        (nameof(PerformanceInfo), PerformanceInfo.Run),
    ];
}