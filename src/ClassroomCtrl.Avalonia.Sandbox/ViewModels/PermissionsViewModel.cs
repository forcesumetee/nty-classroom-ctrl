using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>Phase 32-D — one onboarding row: a permission's name/purpose/tier, a live status pill,
/// and Grant / Recheck. Grant runs the (possibly blocking) native request off the UI thread.</summary>
public partial class PermissionRowViewModel : ObservableObject
{
    private readonly PermInfo _info;

    public PermissionRowViewModel(PermInfo info) { _info = info; Refresh(); }

    public string Name => _info.Name;
    public string Purpose => _info.Purpose;
    public string TierLabel => _info.Tier == PermTier.Required ? "Required" : "Optional";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusClass))]
    private PermState state;

    public string Glyph => PermissionPresenter.Pill(State).glyph;
    public string StatusText => PermissionPresenter.Pill(State).text;
    public string StatusClass => PermissionPresenter.Pill(State).cls;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string hint = "";
    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <summary>Re-poll the live TCC status (never prompts).</summary>
    public void Refresh() => State = Permissions.Check(_info.Id);

    [RelayCommand]
    private void Recheck() => Refresh();

    [RelayCommand]
    private async Task Grant()
    {
        Hint = "";
        State = await Permissions.RequestAsync(_info.Id);
        if (_info.NeedsRelaunch)
        {
            // Screen Recording: TCC caches the capture entitlement at LAUNCH, so a grant only takes
            // effect after a restart — say so regardless of what the call returned this run.
            Hint = "Screen Recording only takes effect after you RESTART the app "
                 + "(macOS caches it at launch). Grant it in System Settings if prompted, then restart.";
        }
        else if (State == PermState.Denied)
        {
            Hint = $"Denied — enable {_info.Name} in System Settings › Privacy & Security, then Recheck.";
        }
    }
}

/// <summary>Phase 32-D — first-run onboarding: the four permission rows + run-readiness (gated only
/// on Screen Recording; optional perms absent just disable their features, never block).</summary>
public partial class PermissionsViewModel : ObservableObject
{
    public ObservableCollection<PermissionRowViewModel> Rows { get; } = new();

    public PermissionsViewModel()
    {
        foreach (var info in Permissions.All)
        {
            var row = new PermissionRowViewModel(info);
            if (info.Id == PermId.Screen)
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PermissionRowViewModel.State))
                    {
                        OnPropertyChanged(nameof(IsReadyToRun));
                        OnPropertyChanged(nameof(ReadyText));
                    }
                };
            Rows.Add(row);
        }
    }

    private PermissionRowViewModel Screen => Rows[0];   // Permissions.All[0] is Screen (primary)

    public bool IsReadyToRun => PermissionPresenter.IsReadyToRun(Screen.State);

    public string ReadyText => IsReadyToRun
        ? "Ready — Screen Recording is granted; the teacher can see your screen."
        : "Screen Recording isn't granted yet — the teacher can't see your screen (the app still connects and locks). Optional permissions add camera, mic, and lock hardening.";

    [RelayCommand]
    private void RecheckAll()
    {
        foreach (var r in Rows) r.Refresh();
    }
}
