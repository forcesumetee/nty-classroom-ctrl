namespace ClassroomCtrl.Shared.Wpf.Roles;

/// <summary>
/// Phase 16-D — capability contract for Conference Mode role gating.  Both
/// Host (teacher today) and Participant (student today) implement this; the
/// Conference UI binds Visibility/IsEnabled to <c>CanX</c> properties via
/// the standard MVVM DataContext chain (no extra view-locator wiring).
///
/// Naming convention: every property is <c>Can{Action}</c> + non-nullable
/// bool.  Returning IsEnabled vs Visibility from the same property is
/// intentional — XAML chooses by which Visibility converter it binds with.
/// Per 16-D primer §9, we prefer hidden (Collapsed) over disabled for the
/// admin actions a student should never see; disabled would still announce
/// the capability via screen readers.
///
/// Self-controls are properties every participant has (both roles set true).
/// Host-only controls flip to false on ParticipantRole.  A future paid /
/// classroom-config tier can add intermediate roles (e.g. CoHost) by
/// implementing the interface with a different mix.
/// </summary>
public interface IConferenceRole
{
    // ─────── Self-controls (every participant has these) ───────

    /// <summary>True iff this participant can toggle their own mic.</summary>
    bool CanToggleOwnMic { get; }

    /// <summary>True iff this participant can toggle their own camera.</summary>
    bool CanToggleOwnCam { get; }

    /// <summary>True iff this participant can send chat messages into the
    /// Conference sidebar's chat tab.</summary>
    bool CanSendChat { get; }

    /// <summary>True iff this participant can send transient emoji reactions
    /// (👍 ❤️ 😂 😮 😢).  False would hide the toolbar ⋮ More button.</summary>
    bool CanSendReaction { get; }

    /// <summary>True iff this participant can raise their own hand.  False
    /// would hide the toolbar ✋ button (today both roles are true; reserved
    /// for future custom-class policies).</summary>
    bool CanRaiseOwnHand { get; }

    /// <summary>True iff this participant can leave the Conference for
    /// themselves.  Both roles today are true; the toolbar 📞 button label
    /// flips between "End for All" and "Leave" based on <see cref="CanEndForAll"/>.</summary>
    bool CanLeaveConference { get; }

    /// <summary>True iff this participant can share their screen into the
    /// in-frame Conference share view (16-B+).  Today: Host always; Participant
    /// follows Zoom defaults — false (host gates share permission).  Future:
    /// per-session policy toggle.</summary>
    bool CanShareScreen { get; }

    // ─────── Host-only controls (Participant returns false) ───────

    /// <summary>True iff this participant can mute another participant's mic
    /// without the recipient's consent.  Drives the per-row "Mute" admin
    /// button in the Sidebar's Participants tab.</summary>
    bool CanMuteOthers { get; }

    /// <summary>True iff this participant can End the Conference for everyone
    /// (vs Leave for themselves).  Flips the toolbar 📞 button label between
    /// "End for All" and "Leave".</summary>
    bool CanEndForAll { get; }

    /// <summary>True iff this participant can force another participant's
    /// camera off without consent.  Drives the per-row "Force Cam" admin
    /// button in the Sidebar's Participants tab.</summary>
    bool CanForceParticipantCam { get; }

    /// <summary>True iff this participant can dismiss a peer's raised hand
    /// from the queue (semantic = "you've been recognized — please speak").
    /// Drives the per-row "Recognize" button in the Sidebar's Raised Hands
    /// section.</summary>
    bool CanRecognizeHand { get; }

    /// <summary>True iff this participant can pin a tile so the pin
    /// propagates to every other participant's gallery (Spotlight semantics).
    /// Drives the tile context menu's "Pin for everyone" entry.  Local
    /// per-participant pin is always available regardless of role.</summary>
    bool CanPinForAll { get; }

    /// <summary>True iff this participant can see the Sidebar's Participants
    /// tab admin controls overall.  Today equivalent to "is Host".  Pulled
    /// out as its own flag so a future tier (e.g. CoHost without
    /// CanMuteOthers) can selectively gate.</summary>
    bool CanManageParticipants { get; }
}

/// <summary>
/// Phase 16-D — full-capability role assigned to the Conference owner
/// (today: teacher only).  Every <c>Can*</c> returns true.
/// </summary>
public sealed class HostRole : IConferenceRole
{
    public bool CanToggleOwnMic         => true;
    public bool CanToggleOwnCam         => true;
    public bool CanSendChat             => true;
    public bool CanSendReaction         => true;
    public bool CanRaiseOwnHand         => true;
    public bool CanLeaveConference      => true;
    public bool CanShareScreen          => true;
    public bool CanMuteOthers           => true;
    public bool CanEndForAll            => true;
    public bool CanForceParticipantCam  => true;
    public bool CanRecognizeHand        => true;
    public bool CanPinForAll            => true;
    public bool CanManageParticipants   => true;
}

/// <summary>
/// Phase 16-D — restricted-capability role assigned to non-host
/// participants (today: student).  Self-controls true; host-only controls
/// false.  Matches Zoom / Meet defaults — a participant manages their own
/// mic/cam/chat/reaction/hand but never another participant's state.
///
/// CanShareScreen defaults to false (Zoom default — host must enable
/// participant share); a future per-session policy toggle could let the
/// host flip this on.
/// </summary>
public sealed class ParticipantRole : IConferenceRole
{
    public bool CanToggleOwnMic         => true;
    public bool CanToggleOwnCam         => true;
    public bool CanSendChat             => true;
    public bool CanSendReaction         => true;
    public bool CanRaiseOwnHand         => true;
    public bool CanLeaveConference      => true;
    // Phase 20 (v1.1) — participants CAN initiate a share request now.
    // The toolbar 🖥 button no longer hides on Participant; instead it
    // toggles through the request → approved → sharing state machine
    // (StudentConferenceShellViewModel.ShareState) backed by wire codes
    // 0x0686 / 0x0687.  Teacher still gates approval, so the customer's
    // "host controls share" expectation holds — what changed is the
    // affordance for the student to ASK.
    public bool CanShareScreen          => true;
    public bool CanMuteOthers           => false;
    public bool CanEndForAll            => false;
    public bool CanForceParticipantCam  => false;
    public bool CanRecognizeHand        => false;
    public bool CanPinForAll            => false;
    public bool CanManageParticipants   => false;
}
