using System.Runtime.CompilerServices;

// TT-7-E — expose the dispatcher-free attribution methods (TeacherGridViewModel.ApplyHandRaise /
// ApplyReaction, ChatViewModel.ApplyIncomingChat) to the committed MockStudent gate without
// widening the public API. MockStudent is the headless gate harness (tools/MockStudent).
[assembly: InternalsVisibleTo("MockStudent")]
