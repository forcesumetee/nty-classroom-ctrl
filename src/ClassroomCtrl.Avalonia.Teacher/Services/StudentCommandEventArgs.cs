using System;
using ClassroomCtrl.Teacher.Core;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>Carries the chosen <see cref="StudentCommand"/> from a tile's context menu up to
/// the window (which resolves the target student from the sender's DataContext), mirroring
/// TT-3's DoubleTapped→StudentActivated flow. This is the UI event contract, so it stays in
/// the Teacher app; the <see cref="StudentCommand"/> vocabulary itself lives in Teacher.Core.</summary>
public sealed class StudentCommandEventArgs : EventArgs
{
    public StudentCommand Command { get; }
    public StudentCommandEventArgs(StudentCommand command) => Command = command;
}
