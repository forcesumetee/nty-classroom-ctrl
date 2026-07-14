using System;
using Avalonia.Input;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>TT-6-B — carries the keyboard modifiers of a left-click on a tile up to the
/// window, which resolves the target from the sender's DataContext and decodes the modifiers
/// into the macOS selection idiom (plain = select-only, ⌘ = toggle, Shift = range). The
/// decode lives in the app because it needs Avalonia <see cref="KeyModifiers"/>; the selection
/// LOGIC it drives is the UI-agnostic TileSelectionModel in Teacher.Core.</summary>
public sealed class SelectionRequestedEventArgs : EventArgs
{
    public KeyModifiers Modifiers { get; }
    public SelectionRequestedEventArgs(KeyModifiers modifiers) => Modifiers = modifiers;
}
