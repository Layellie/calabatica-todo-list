using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

/// <summary>
/// İlk açılış rehberi (3 adım: hızlı ekleme sözdizimi, klavye + odak, mini mod + veri). Yalnızca ilk kurulumda
/// kendiliğinden açılır; sonra başlıktaki "?" ile. Yüksek kontrast: Windows ayarı açıksa tema otomatik.
/// </summary>
public sealed partial class MainViewModel
{
    public const int OnboardingSteps = 3;

    [ObservableProperty]
    private bool _isOnboardingOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnboardingTitle), nameof(OnboardingBody), nameof(OnboardingStepText), nameof(IsLastOnboardingStep), nameof(IsFirstOnboardingStep))]
    [NotifyCanExecuteChangedFor(nameof(PreviousOnboardingStepCommand))]
    private int _onboardingStep;

    public string OnboardingTitle => _loc[$"Onb_{OnboardingStep + 1}_Title"];

    public string OnboardingBody => _loc[$"Onb_{OnboardingStep + 1}_Body"];

    public string OnboardingStepText => $"{OnboardingStep + 1} / {OnboardingSteps}";

    public bool IsFirstOnboardingStep => OnboardingStep == 0;

    public bool IsLastOnboardingStep => OnboardingStep == OnboardingSteps - 1;

    [RelayCommand]
    private void OpenOnboarding()
    {
        CloseOverlays();
        OnboardingStep = 0;
        IsOnboardingOpen = true;
    }

    /// <summary>İleri; son adımda kapatır (ve bir daha kendiliğinden açılmaz).</summary>
    [RelayCommand]
    private void NextOnboardingStep()
    {
        if (IsLastOnboardingStep)
        {
            CloseOnboarding();
            return;
        }

        OnboardingStep++;
    }

    private bool CanGoBackOnboarding() => OnboardingStep > 0;

    [RelayCommand(CanExecute = nameof(CanGoBackOnboarding))]
    private void PreviousOnboardingStep() => OnboardingStep--;

    [RelayCommand]
    private void CloseOnboarding()
    {
        IsOnboardingOpen = false;
        if (!_settings.OnboardingDone)
        {
            _settings.OnboardingDone = true;
            SaveSettingsIfReady();
        }
    }

    // ───────── Yüksek kontrast ─────────

    private bool _systemHighContrast;

    /// <summary>
    /// Windows yüksek kontrast modu (View, SystemParameters'tan bildirir). Açıkken seçili temadan bağımsız
    /// yüksek kontrast paleti kullanılır; kayıtlı tema seçimi değişmez, mod kapanınca geri döner.
    /// </summary>
    public void SetSystemHighContrast(bool enabled)
    {
        _systemHighContrast = enabled;
        ApplyEffectiveTheme();
    }

    private void ApplyEffectiveTheme() =>
        CurrentTheme = ThemePalette.Get(_systemHighContrast ? ThemeId.HighContrast : _settings.Theme);
}
