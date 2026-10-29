// ============================================================================
// FILE: src/5_BreadboardAOI.App/Workspace/WorkspaceLayoutStore.cs
// PURPOSE: Remembers the operator's Adobe-style dock layout between sessions.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;

namespace BreadboardAOI.App.Workspace;

/// <summary>Persisted dock visibility, widths and the active left-dock tab.</summary>
public sealed record WorkspaceLayoutState(
    bool LeftDockVisible = true,
    bool RightDockVisible = true,
    double LeftDockWidth = 270,
    double RightDockWidth = 338,
    int ActiveLeftTab = 0);

/// <summary>
/// Small JSON file under %LOCALAPPDATA%\BreadboardAOI. Layout is cosmetic, so a
/// read/write failure must never stop the workstation from starting.
/// </summary>
public static class WorkspaceLayoutStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BreadboardAOI",
        "workspace-layout.json");

    public static WorkspaceLayoutState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new WorkspaceLayoutState();

            WorkspaceLayoutState? state = JsonSerializer.Deserialize<WorkspaceLayoutState>(
                File.ReadAllText(FilePath),
                Options);

            if (state is null)
                return new WorkspaceLayoutState();

            return state with
            {
                LeftDockWidth = Math.Clamp(state.LeftDockWidth, 200, 460),
                RightDockWidth = Math.Clamp(state.RightDockWidth, 240, 520),
                ActiveLeftTab = Math.Clamp(state.ActiveLeftTab, 0, 3)
            };
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException)
        {
            return new WorkspaceLayoutState();
        }
    }

    public static void Save(WorkspaceLayoutState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            // Layout is a convenience; never surface a disk failure here.
        }
    }
}
