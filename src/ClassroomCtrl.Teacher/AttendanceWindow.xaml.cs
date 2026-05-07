using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Models.Roster;
using ClosedXML.Excel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 7 Section D — Mark Attendance dialog.  One row per enrolled student
/// with Present/Absent/Late/Excused radios.  Save flushes the row Status onto
/// the matching EnrolledStudent and persists the roster to disk so re-open
/// (within session OR after app restart) shows the same selections.
///
/// Phase 7.1 hotfix: was overwriting the saved Status on every re-open by
/// always re-deriving it from connectedMachineNames; now the constructor only
/// uses that connected-default for students whose HasBeenMarked is false.
/// Also sanitizes the class name when building the export filename so class
/// names with "/" (e.g. "5/2") don't blow up SaveFileDialog.
/// </summary>
public partial class AttendanceWindow : Window
{
    public class AttendanceRow : INotifyPropertyChanged
    {
        // RowId scopes the 4 RadioButtons to a single per-row group; using a Guid
        // avoids accidental collisions with empty MachineName fields and keeps the
        // group stable across DataGrid virtualization.
        public string RowId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>Phase 7.2: index into the roster's Students list at the
        /// moment the dialog was opened.  Used as the matching key in
        /// Save_Click because none of FullName/MachineName/StudentNumber is
        /// guaranteed unique within a roster (e.g. two disconnected students
        /// both have MachineName="" — and Lead has hit identical-FullName
        /// rows during testing).  Safe because AttendanceWindow is modal:
        /// the Students list cannot be reordered while it's open.</summary>
        public int RosterIndex { get; init; }

        public string FullName { get; init; } = "";
        public string MachineName { get; init; } = "";
        public string StudentNumber { get; init; } = "";

        private AttendanceStatus _status = AttendanceStatus.Present;
        public AttendanceStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                System.Diagnostics.Debug.WriteLine(
                    $"[ATTENDANCE STATUS] row='{FullName}' RowId={RowId.Substring(0, 6)} {_status} → {value}");
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsPresent));
                OnPropertyChanged(nameof(IsAbsent));
                OnPropertyChanged(nameof(IsLate));
                OnPropertyChanged(nameof(IsExcused));
            }
        }

        public bool IsPresent
        {
            get => Status == AttendanceStatus.Present;
            set { System.Diagnostics.Debug.WriteLine($"[ATTENDANCE BIND] row='{FullName}' IsPresent set={value}"); if (value) Status = AttendanceStatus.Present; }
        }
        public bool IsAbsent
        {
            get => Status == AttendanceStatus.Absent;
            set { System.Diagnostics.Debug.WriteLine($"[ATTENDANCE BIND] row='{FullName}' IsAbsent set={value}"); if (value) Status = AttendanceStatus.Absent; }
        }
        public bool IsLate
        {
            get => Status == AttendanceStatus.Late;
            set { System.Diagnostics.Debug.WriteLine($"[ATTENDANCE BIND] row='{FullName}' IsLate set={value}"); if (value) Status = AttendanceStatus.Late; }
        }
        public bool IsExcused
        {
            get => Status == AttendanceStatus.Excused;
            set { System.Diagnostics.Debug.WriteLine($"[ATTENDANCE BIND] row='{FullName}' IsExcused set={value}"); if (value) Status = AttendanceStatus.Excused; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly ClassRoster _roster;
    private readonly List<AttendanceRow> _rows;

    public AttendanceWindow(ClassRoster roster, ISet<string> connectedMachineNames)
    {
        InitializeComponent();
        _roster = roster;
        ClassNameText.Text = roster.ClassName;

        // Phase 7.3 diagnostic — verify the dialog received the same ClassRoster
        // reference that App.Roster.ActiveRoster currently holds (so in-memory
        // mutations actually propagate back), and snapshot each EnrolledStudent's
        // saved AttendanceStatus + HasBeenMarked at open time.
        System.Diagnostics.Debug.WriteLine(
            $"[ATTENDANCE OPEN] roster='{roster.ClassName}' " +
            $"_roster.HashCode={roster.GetHashCode()} " +
            $"App.Roster.ActiveRoster.HashCode={App.Roster?.ActiveRoster?.GetHashCode() ?? -1} " +
            $"sameRef={ReferenceEquals(roster, App.Roster?.ActiveRoster)}");

        _rows = roster.Students.Select((s, i) =>
        {
            var initial = s.HasBeenMarked
                ? s.AttendanceStatus
                : (!string.IsNullOrWhiteSpace(s.MachineName)
                   && connectedMachineNames.Contains(s.MachineName)
                       ? AttendanceStatus.Present
                       : AttendanceStatus.Absent);
            System.Diagnostics.Debug.WriteLine(
                $"[ATTENDANCE OPEN] idx={i} name='{s.FullName}' " +
                $"savedStatus={s.AttendanceStatus} marked={s.HasBeenMarked} " +
                $"machine='{s.MachineName}' connected={connectedMachineNames.Contains(s.MachineName ?? "")} " +
                $"→ initialRowStatus={initial} studentHash={s.GetHashCode()}");
            return new AttendanceRow
            {
                RosterIndex = i,
                FullName = s.FullName,
                MachineName = s.MachineName,
                StudentNumber = s.StudentNumber,
                Status = initial,
            };
        }).ToList();

        // Wire PropertyChanged so the footer stats refresh whenever any row toggles.
        foreach (var r in _rows) r.PropertyChanged += (_, _) => UpdateStats();

        StudentsGrid.ItemsSource = _rows;
        UpdateStats();
    }

    private void UpdateStats()
    {
        int p = _rows.Count(r => r.Status == AttendanceStatus.Present);
        int a = _rows.Count(r => r.Status == AttendanceStatus.Absent);
        int l = _rows.Count(r => r.Status == AttendanceStatus.Late);
        int e = _rows.Count(r => r.Status == AttendanceStatus.Excused);
        StatsText.Text = Loc.Format("Lbl_AttendanceStats", p, a, l, e);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Phase 7.1: write the full Status (not just IsPresent) and flag the
        // student as marked so the next dialog open restores the radios.
        // Mirror Status → IsPresent for legacy consumers (Chat_AttendanceMarked).
        // Phase 7.2: match rows back to EnrolledStudent objects by RosterIndex
        // (positional) instead of by FullName+MachineName.  The previous
        // FirstOrDefault-by-fields collapsed when two disconnected students
        // shared an empty MachineName *and* an identical FullName — both rows
        // wrote to the same student, so the second's Status overwrote the
        // first's, then on reopen both rows showed the second's Status (or
        // back to the connected default if HasBeenMarked never propagated).
        // Phase 7.3 diagnostic — snapshot every row's Status as Save sees it,
        // so we can compare against what the user clicked in the UI vs what
        // ends up in match.AttendanceStatus + on disk.
        System.Diagnostics.Debug.WriteLine(
            $"[ATTENDANCE SAVE] _roster.HashCode={_roster.GetHashCode()} " +
            $"App.Roster.ActiveRoster.HashCode={App.Roster?.ActiveRoster?.GetHashCode() ?? -1} " +
            $"sameRef={ReferenceEquals(_roster, App.Roster?.ActiveRoster)} " +
            $"rowCount={_rows.Count}");
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.RosterIndex < 0 || row.RosterIndex >= _roster.Students.Count)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ATTENDANCE SAVE] SKIP idx={i} RosterIndex={row.RosterIndex} out of range (Students.Count={_roster.Students.Count})");
                continue;
            }
            var match = _roster.Students[row.RosterIndex];
            System.Diagnostics.Debug.WriteLine(
                $"[ATTENDANCE SAVE] idx={i} RosterIndex={row.RosterIndex} " +
                $"row.Status={row.Status} → student='{match.FullName}' " +
                $"studentHash={match.GetHashCode()}");
            match.AttendanceStatus = row.Status;
            match.IsPresent = row.Status == AttendanceStatus.Present;
            match.HasBeenMarked = true;
        }

        // Persist to disk so the saved selections survive an app restart.  Best-
        // effort: roster save failure should not block the dialog from closing
        // (in-memory state is still correct for the rest of the session).
        try
        {
            App.Roster?.SaveRoster(_roster);
            System.Diagnostics.Debug.WriteLine($"[ATTENDANCE SAVE] SaveRoster OK rosterId={_roster.Id:N}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ATTENDANCE SAVE] SaveRoster FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        // Phase 7.1: SaveFileDialog rejects any FileName with Path.GetInvalidFileNameChars
        // (notably "/") — class "5/2" → "5_2" so the suggested name is always valid.
        var safeClassName = SanitizeFileName(_roster.ClassName);
        var dlg = new SaveFileDialog
        {
            Title = Loc.Get("Btn_ExportExcel"),
            Filter = "Excel files (*.xlsx)|*.xlsx",
            FileName = $"attendance_{safeClassName}_{DateTime.Now:yyyy-MM-dd_HHmm}.xlsx",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Attendance");

            // Sheet header rows — class + date stamp so the .xlsx file is
            // self-describing without relying on the filename.
            ws.Cell(1, 1).Value = Loc.Get("Hdr_MarkAttendance");
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Range(1, 1, 1, 5).Merge();

            ws.Cell(2, 1).Value = $"{_roster.ClassName} — {DateTime.Now:yyyy-MM-dd HH:mm}";
            ws.Range(2, 1, 2, 5).Merge();

            // Column headers
            string[] headers =
            {
                Loc.Get("Lbl_FullName"),
                Loc.Get("Lbl_StudentNumber"),
                Loc.Get("Lbl_RosterCol_Machine"),
                Loc.Get("Lbl_AttendanceStatus"),
                "Date",
            };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(4, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
            }

            int row = 5;
            foreach (var r in _rows)
            {
                ws.Cell(row, 1).Value = r.FullName;
                ws.Cell(row, 2).Value = r.StudentNumber;
                ws.Cell(row, 3).Value = r.MachineName;
                ws.Cell(row, 4).Value = StatusLabel(r.Status);
                ws.Cell(row, 5).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(dlg.FileName);

            MessageBox.Show(
                Loc.Format("Msg_AttendanceExported", dlg.FileName),
                Loc.Get("Lbl_Success"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.Get("Btn_ExportExcel"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Replaces every char in Path.GetInvalidFileNameChars() with '_' so a class
    /// name like "5/2" becomes "5_2".  Trim trailing whitespace because some
    /// callers feed display names with stray spaces.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "class";
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid)).Trim();
    }

    private static string StatusLabel(AttendanceStatus s) => s switch
    {
        AttendanceStatus.Present => Loc.Get("Lbl_Present"),
        AttendanceStatus.Absent => Loc.Get("Lbl_Absent"),
        AttendanceStatus.Late => Loc.Get("Lbl_Late"),
        AttendanceStatus.Excused => Loc.Get("Lbl_Excused"),
        _ => "",
    };
}
