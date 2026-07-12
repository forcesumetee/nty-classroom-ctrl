using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox-only host VM (Phase 24.3).  Builds a spread of ConferenceTile states
/// so every ported style-class / trigger path is visible at once for the
/// side-by-side visual comparison against the Windows tile.
/// </summary>
public class MainWindowViewModel
{
    public ObservableCollection<ConferenceTileViewModel> Tiles { get; } = new();

    /// <summary>Phase 25.1 — demo VM driving the ported ConferenceToolbar.</summary>
    public ConferenceToolbarDemoViewModel Toolbar { get; } = new();

    /// <summary>Phase 25.1-D — tiles shown in the bottom-bar demo tab; a picked
    /// reaction floats over DemoTileA (fulfils the 24.3 float-on-tile design).</summary>
    public ConferenceTileViewModel DemoTileA { get; } =
        new(Guid.NewGuid(), "Pim", isSelf: true) { IsHost = true, IsMicLive = true };
    public ConferenceTileViewModel DemoTileB { get; } =
        new(Guid.NewGuid(), "Nok") { IsSpeaking = true, IsMicLive = true, IsCamLive = true };

    public MainWindowViewModel()
    {
        // Reaction picked in the toolbar Flyout → float it over DemoTileA. Clear
        // first so re-picking the same emoji still raises PropertyChanged (and
        // re-runs the animation).
        Toolbar.ReactionPicked += emoji =>
        {
            DemoTileA.CurrentReactionEmoji = "";
            DemoTileA.CurrentReactionEmoji = emoji;
        };
        // 1) Teacher self — HOST badge, mic live (green dot + white mic), cam off (red).
        Tiles.Add(new ConferenceTileViewModel(Guid.NewGuid(), "Teacher", isSelf: true)
        {
            IsHost = true, IsMicLive = true, IsCamLive = false,
        });

        // 2) Active speaker — yellow ring (speaking class), mic + cam live.
        Tiles.Add(new ConferenceTileViewModel(Guid.NewGuid(), "Somchai")
        {
            IsSpeaking = true, IsMicLive = true, IsCamLive = true,
        });

        // 3) Hand raised — amber hand badge; mic muted (red).
        Tiles.Add(new ConferenceTileViewModel(Guid.NewGuid(), "Ploy")
        {
            IsHandRaised = true, IsMicLive = false, IsCamLive = false,
        });

        // 4) Pinned — purple ring (pinned class); cam off.
        Tiles.Add(new ConferenceTileViewModel(Guid.NewGuid(), "Anong")
        {
            IsPinned = true, IsMicLive = false, IsCamLive = false,
        });

        // 5) Live video frame — exercises the .live class (dim bg) + Image path;
        //    name placeholder hides because JpegFrame != null.
        Tiles.Add(new ConferenceTileViewModel(Guid.NewGuid(), "Kittipong")
        {
            IsMicLive = true, IsCamLive = true,
            JpegFrame = MakeSolidBitmap(320, 180, 0xFF1E6F6F), // teal BGRA
        });

        // 6) Reaction glyph shown (static — animation deferred in the port).
        Tiles.Add(new ConferenceTileViewModel(Guid.NewGuid(), "Malee")
        {
            CurrentReactionEmoji = "👍", IsMicLive = false, IsCamLive = true,
        });
    }

    /// <summary>Generate a solid-color Avalonia Bitmap without unsafe code
    /// (Marshal.Copy into the locked framebuffer).  Stands in for a decoded
    /// webcam JPEG so the live-video binding path is visible in the sandbox.</summary>
    private static Bitmap MakeSolidBitmap(int w, int h, uint bgra)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Premul);
        var buf = new byte[w * h * 4];
        byte b = (byte)(bgra & 0xFF), g = (byte)((bgra >> 8) & 0xFF),
             r = (byte)((bgra >> 16) & 0xFF), a = (byte)((bgra >> 24) & 0xFF);
        for (int i = 0; i < buf.Length; i += 4) { buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = a; }
        using (var fb = wb.Lock())
            Marshal.Copy(buf, 0, fb.Address, buf.Length);
        return wb;
    }
}
