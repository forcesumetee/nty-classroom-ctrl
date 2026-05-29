using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Shared.Wpf.ViewModels;

/// <summary>
/// Phase 15-C — host VM for the Conference gallery.  Owns the tile collection,
/// the pin state, and the paging state for 17+-tile classrooms.  Used by both
/// teacher's <c>ConferenceView</c> (sourced from <c>MainViewModel.Students</c>
/// + self) and student's <c>ConferenceGalleryWindow</c> (single-tile today;
/// Phase 16-C lights up peer-cam fan-out).  The gallery <c>UserControl</c> is
/// dumb — it just binds.
///
/// Phase 16-B step 3 — moved from Teacher/ViewModels/ to Shared.Wpf so
/// Student.Agent can host the same gallery surface via its
/// StudentConferenceShellViewModel.
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

    /// <summary>Phase 16-B step 5 — UniformGrid column count for the current
    /// page's tile count, computed via the same breakpoint table as
    /// <c>ParticipantCountToColumnsConverter</c>.  Exists as a plain int
    /// property (not a collection-Count binding inside
    /// ItemsPanelTemplate) so the binding resolves reliably at panel
    /// realize-time AND re-evaluates on every <see cref="RebuildPageTiles"/>
    /// — fixes the 15-C symptom where tiles rendered off-viewport because
    /// the UniformGrid stalled at Columns=1.</summary>
    public int ColumnsForPage
    {
        get
        {
            int n = PageTiles.Count;
            if (n <= 1) return 1;
            if (n == 2) return 2;
            if (n <= 4) return 2;
            if (n <= 9) return 3;
            return 4;
        }
    }

    /// <summary>Phase 15-E — tiles whose participant currently has a hand
    /// raised, ordered by raise timestamp (oldest first = top of queue).
    /// Bound by the Conference sidebar's Participants tab when the teacher
    /// wants to recognize the next student.  Recomputed by
    /// <see cref="RefreshRaisedHandQueue"/> whenever a tile's
    /// IsHandRaised flips.</summary>
    public ObservableCollection<ConferenceTileViewModel> RaisedHandQueue { get; } = new();

    // ─────── Phase 16-B+ : In-frame Conference share — gallery state ───────

    /// <summary>Phase 16-B+ — endpoint id of the participant currently
    /// presenting an in-frame screen share, or null when no share is active.
    /// Set by the receiver-side ConferenceShareStart dispatch (Student.Agent
    /// MainWindow or Teacher MainViewModel); cleared on ConferenceShareStop.
    /// Drives <see cref="IsShareActive"/> + the gallery's layout switch
    /// between tile-mode and share-mode.</summary>
    [ObservableProperty] private Guid? activeShareEndpointId;

    /// <summary>Phase 16-B+ — display name of the active sharer.  Surfaced in
    /// the share view's "X is sharing" banner so the source can be identified
    /// without resolving sender→name against the participant collection.</summary>
    [ObservableProperty] private string activeShareSourceName = "";

    /// <summary>Phase 16-B+ — latest decoded share frame, bound directly to
    /// the share view's primary Image.Source.  Null until the first frame
    /// after Start arrives; cleared on Stop.  BitmapSource is the WPF type
    /// frozen on the decode thread — same pattern as
    /// <see cref="ConferenceTileViewModel.JpegFrame"/>.</summary>
    [ObservableProperty] private BitmapSource? activeShareFrame;

    /// <summary>Phase 16-B+ — true while a Conference share is in flight.
    /// Computed from <see cref="ActiveShareEndpointId"/>.</summary>
    public bool IsShareActive => ActiveShareEndpointId.HasValue;

    partial void OnActiveShareEndpointIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsShareActive));
    }

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
    /// Single-pass diff: clear + add (small N keeps this trivial).
    ///
    /// Phase 16-B step 5 — also raises PropertyChanged for ColumnsForPage so
    /// the gallery's UniformGrid re-evaluates its column count after every
    /// tile-collection change.</summary>
    public void RebuildPageTiles()
    {
        int start = (Math.Max(1, CurrentPage) - 1) * TilesPerPage;
        int end = Math.Min(Tiles.Count, start + TilesPerPage);
        PageTiles.Clear();
        for (int i = start; i < end; i++) PageTiles.Add(Tiles[i]);
        OnPropertyChanged(nameof(ColumnsForPage));
    }
}
