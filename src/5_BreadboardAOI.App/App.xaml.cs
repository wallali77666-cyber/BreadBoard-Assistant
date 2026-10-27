using System.Windows;

namespace BreadboardAOI.App;

/// <summary>
/// Workstation shell. The docking layout, MVVM graph, and SkiaSharp viewport are
/// wired up in Phase 5; this shell only exists so the solution builds as a WinExe
/// from Phase 1 onward.
/// </summary>
public partial class App : Application
{
}
