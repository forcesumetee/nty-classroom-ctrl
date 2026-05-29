using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Teacher.ViewModels;

/// <summary>
/// Phase 15-C — per-tile state for the Conference gallery.  Each connected
/// participant (student + teacher self) maps to one of these; the parent
/// <see cref="ConferenceGalleryViewModel"/> owns the collection and routes
/// frames + state updates here.
///
/// Properties are kept observable so a XAML DataTemplate can bind directly.
/// Layout decisions (pinned / filmstrip / paged) live on the gallery VM,
/// not on the tile.
/// </summary>
public partial class ConferenceTileViewModel : ObservableObject
{
    /// <summary>Source endpoint id - immutable for the lifetime of the tile.
    /// Used by gallery sync (Phase 15-C step 1) + active-speaker routing
    /// (step 4) + pin clicks (step 5).</summary>
    public Guid EndpointId { get; }

    /// <summary>True for the teacher's own tile.  Drives "(You)" suffix +
    /// the cam-frame routing path (teacher frames come from
    /// ControlServer.TeacherCameraFrameSent rather than the inbound dispatch).</summary>
    public bool IsSelf { get; }

    [ObservableProperty] private string displayName = "";

    /// <summary>Latest JPEG-decoded frame; bound directly to a WPF Image.Source.
    /// Null while the participant's cam is off or before the first frame
    /// arrives.</summary>
    [ObservableProperty] private BitmapSource? jpegFrame;

    [ObservableProperty] private bool isMicLive;
    [ObservableProperty] private bool isSpeaking;
    [ObservableProperty] private bool isCamLive;
    [ObservableProperty] private bool isHandRaised;

    /// <summary>Set by <see cref="ConferenceGalleryViewModel"/> when this tile
    /// is the active pin.  Drives a border accent + governs whether the
    /// layout switches to filmstrip mode.</summary>
    [ObservableProperty] private bool isPinned;

    /// <summary>Phase 15-E — UTC wall-clock at which the participant raised
    /// their hand.  Used by the gallery view-model's RaisedHandQueue to
    /// order entries so the first-raised hand appears at the top of the
    /// teacher's recognize list.  Null while no hand is raised; set
    /// alongside <see cref="IsHandRaised"/> by MainViewModel
    /// .OnHandRaiseReceived.</summary>
    [ObservableProperty] private DateTime? handRaisedAt;

    /// <summary>Phase 15-E step 4 — transient reaction emoji shown floating
    /// over the tile.  Set by MainViewModel.OnReactionReceived for ~3 s,
    /// then cleared by a DispatcherTimer.  Empty string = no reaction.</summary>
    [ObservableProperty] private string currentReactionEmoji = "";

    public ConferenceTileViewModel(Guid endpointId, string displayName, bool isSelf = false)
    {
        EndpointId = endpointId;
        DisplayName = displayName;
        IsSelf = isSelf;
    }
}

/// <summary>
/// Phase 15-C — host VM for the Conference gallery.  Owns the tile collection,
/// the pin state, and the paging state for 17+-tile classrooms.  Used by both
/// teacher's <c>ConferenceView</c> (sourced from <c>MainViewModel.Students</c>
/// + self) and student's <c>ConferenceGalleryWindow</c> (single-tile: the
/// teacher).  The gallery <c>UserControl</c> is dumb — it just binds.
/// </summary>
public partial class ConferenceGalleryViewModel : ObservableObject
{
    public const int TilesPerPage = 16;

    public ObservableCollection<ConferenceTileViewModel> Tiles { get; } = new();

    /// <summary>Null = gallery mode (auto-grid).  Non-null = pinned mode
    /// (large primary tile + filmstrip of the rest).  Set by
    /// <see cref="PinCommand"/>; cleared by clicking the same tile again or
    /// when the pinned participant leaves the conference.</summary>
    [ObservableProperty] private Guid? pinnedEndpointId;

    /// <summary>Convenience flag for the layout XAML — true while a tile is
    /// pinned (filmstrip-mode trigger).</summary>
    public bool IsPinnedView => PinnedEndpointId.HasValue;

    partial void OnPinnedEndpointIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsPinnedView));
        // Mirror the pin flag down onto the affected tiles so the per-tile
        // DataTemplate can DataTrigger off it for the accent border.
        foreach (var t in Tiles)
        {
            t.IsPinned = value.HasValue && t.EndpointId == value.Value;
        }
    }

    /// <summary>1-based current page.  Step 7 (pagination) drives the
    /// view's filter via <see cref="PageTiles"/>.</summary>
    [ObservableProperty] private int currentPage = 1;
    [ObservableProperty] private int totalPages = 1;
    public bool HasMultiplePages => TotalPages > 1;

    /// <summary>Visible tiles for <see cref="CurrentPage"/>.  Rebuilt by
    /// <see cref="RebuildPageTiles"/> on Tiles change or page change so the
    /// gallery's <c>ItemsControl</c> can bind to this collection directly
    /// (rather than filtering Tiles in XAML).</summary>
    public ObservableCollection<ConferenceTileViewModel> PageTiles { get; } = new();

    /// <summary>Step 7 — page-counter text shown next to prev/next buttons.</summary>
    public string PageLabel => $"{CurrentPage} / {TotalPages}";

    /// <summary>Phase 15-E — tiles whose participant currently has a hand
    /// raised, ordered by raise timestamp (oldest first = top of queue).
    /// Bound by the Conference sidebar's Participants tab when the teacher
    /// wants to recognize the next student.  Recomputed by
    /// <see cref="RefreshRaisedHandQueue"/> whenever a tile's
    /// IsHandRaised flips.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ConferenceTileViewModel> RaisedHandQueue { get; }
        = new();

    public void RefreshRaisedHandQueue()
    {
        var ordered = Tiles
            .Where(t => t.IsHandRaised)
            .OrderBy(t => t.HandRaisedAt ?? DateTime.MinValue)
            .ToList();
        RaisedHandQueue.Clear();
        foreach (var t in ordered) RaisedHandQueue.Add(t);
    }

    partial void OnTotalPagesChanged(int value)
    {
        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(PageLabel));
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(PageLabel));
        RebuildPageTiles();
    }

    public IRelayCommand<ConferenceTileViewModel?> PinCommand { get; }
    public IRelayCommand PrevPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }

    public ConferenceGalleryViewModel()
    {
        PinCommand = new RelayCommand<ConferenceTileViewModel?>(t =>
        {
            if (t == null) return;
            // Click already-pinned tile → unpin.  Click new tile → pin it.
            PinnedEndpointId = (PinnedEndpointId == t.EndpointId) ? null : t.EndpointId;
        });
        PrevPageCommand = new RelayCommand(() =>
        {
            if (CurrentPage > 1) CurrentPage--;
        });
        NextPageCommand = new RelayCommand(() =>
        {
            if (CurrentPage < TotalPages) CurrentPage++;
        });
        Tiles.CollectionChanged += (_, _) =>
        {
            RecomputePagination();
            RebuildPageTiles();
        };
        RebuildPageTiles();
    }

    public void RecomputePagination()
    {
        int newTotal = Math.Max(1, (int)Math.Ceiling(Tiles.Count / (double)TilesPerPage));
        if (newTotal != TotalPages) TotalPages = newTotal;
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;
    }

    /// <summary>Step 7 — refresh the <see cref="PageTiles"/> collection from
    /// the current 1-based <see cref="CurrentPage"/> + <see cref="TilesPerPage"/>.
    /// Single-pass diff: clear + add (small N keeps this trivial).</summary>
    public void RebuildPageTiles()
    {
        int start = (Math.Max(1, CurrentPage) - 1) * TilesPerPage;
        int end = Math.Min(Tiles.Count, start + TilesPerPage);
        PageTiles.Clear();
        for (int i = start; i < end; i++) PageTiles.Add(Tiles[i]);
    }
}
