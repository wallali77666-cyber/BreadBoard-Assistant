using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

/// <summary>
/// Validates WPF StaticResource integrity across all XAML files in the application.
/// Prevents runtime System.Windows.StaticResourceExtension crashes caused by missing palette or style keys,
/// syntax variations, or forward-reference ordering bugs.
/// </summary>
public sealed class XamlResourceIntegrityTests
{
    public static readonly HashSet<string> KnownExemptions = new(StringComparer.Ordinal)
    {
        "BoolToVis"
    };

    public static readonly Regex CommentRegex = new(
        @"<!--[\s\S]*?-->",
        RegexOptions.Compiled);

    public static readonly Regex StaticResourceRegex = new(
        @"\{\s*StaticResource\s+(?:ResourceKey\s*=\s*)?[""']?(?<key>[^\s,}""']+)[""']?",
        RegexOptions.Compiled);

    public static readonly Regex ResourceKeyDefinitionRegex = new(
        @"\bx:Key\s*=\s*[""'](?<key>[^""']+)[""']",
        RegexOptions.Compiled);

    [Fact]
    public void All_StaticResource_references_in_app_xaml_files_must_resolve()
    {
        string appDirectory = ResolveAppProjectDirectory();
        Directory.Exists(appDirectory).Should().BeTrue($"App directory must exist: {appDirectory}");

        string themePath = Path.Combine(appDirectory, "Themes", "SpectrumDarkTheme.xaml");
        File.Exists(themePath).Should().BeTrue($"SpectrumDarkTheme.xaml must exist at {themePath}");

        var globalKeys = ExtractGlobalApplicationKeys(appDirectory);
        globalKeys.Should().NotBeEmpty("Global application resources should declare theme and application keys");

        string[] xamlFiles = Directory.GetFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                          segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        xamlFiles.Length.Should().BeGreaterThanOrEqualTo(5, "Expected at least 5 XAML files in BreadboardAOI.App");

        var unresolvedReferences = new List<string>();

        foreach (string file in xamlFiles)
        {
            string relativePath = Path.GetRelativePath(appDirectory, file);
            string rawContent = File.ReadAllText(file);

            var fileUnresolved = FindUnresolvedStaticResources(rawContent, globalKeys);
            foreach (var item in fileUnresolved)
            {
                unresolvedReferences.Add($"{relativePath}:{item.LineNumber} references missing StaticResource '{item.Key}'");
            }
        }

        unresolvedReferences.Should().BeEmpty(
            "all XAML StaticResource references must resolve to SpectrumDarkTheme.xaml, a local resource, or a known exemption.\n" +
            string.Join("\n", unresolvedReferences));
    }

    [Fact]
    public void SpectrumDarkTheme_contains_required_palette_keys()
    {
        string appDirectory = ResolveAppProjectDirectory();
        string themePath = Path.Combine(appDirectory, "Themes", "SpectrumDarkTheme.xaml");
        string themeContent = StripCommentsPreservingLineBreaks(File.ReadAllText(themePath));
        var themeKeys = ExtractDefinedKeys(themeContent);

        string[] requiredKeys =
        [
            "AccentBlueColor",
            "AccentBlueBrush",
            "AccentCyanColor",
            "AccentCyanBrush",
            "BorderSubtleColor",
            "BorderSubtleBrush",
            "PasteboardColor",
            "PasteboardBrush",
            "PanelBackgroundColor",
            "PanelBackgroundBrush",
            "ToolbarSurfaceColor",
            "ToolbarSurfaceBrush",
            "DrcPassBrush",
            "DrcWarningBrush",
            "DrcCriticalBrush"
        ];

        foreach (string key in requiredKeys)
        {
            themeKeys.Should().Contain(key, $"SpectrumDarkTheme.xaml should declare {key}");
        }
    }

    [Fact]
    public void SpectrumDarkTheme_has_no_forward_references_within_file()
    {
        string appDirectory = ResolveAppProjectDirectory();
        string themePath = Path.Combine(appDirectory, "Themes", "SpectrumDarkTheme.xaml");
        string content = StripCommentsPreservingLineBreaks(File.ReadAllText(themePath));

        var keyDefMatches = ResourceKeyDefinitionRegex.Matches(content);
        var staticResMatches = StaticResourceRegex.Matches(content);

        var definedKeys = new List<(int Index, string Key)>();
        foreach (Match match in keyDefMatches)
        {
            definedKeys.Add((match.Index, match.Groups["key"].Value));
        }

        var forwardRefErrors = new List<string>();

        foreach (Match match in staticResMatches)
        {
            string refKey = match.Groups["key"].Value;
            int refIndex = match.Index;

            bool definedBefore = definedKeys.Any(d => d.Index < refIndex && d.Key == refKey);

            if (!definedBefore && !KnownExemptions.Contains(refKey))
            {
                int lineNumber = GetLineNumber(content, refIndex);
                forwardRefErrors.Add($"SpectrumDarkTheme.xaml:{lineNumber} references '{refKey}' before its definition.");
            }
        }

        forwardRefErrors.Should().BeEmpty(
            "WPF requires resource keys in a ResourceDictionary to be defined before any StaticResource references them.\n" +
            string.Join("\n", forwardRefErrors));
    }

    [Fact]
    public void Missing_resource_detection_reports_unresolved_key()
    {
        const string mockXaml = @"
<Grid Background=""{StaticResource NonExistentBrush}"">
    <TextBlock Text=""Hi"" Foreground=""{StaticResource BoolToVis}"" />
</Grid>";

        var globalKeys = new HashSet<string>(StringComparer.Ordinal) { "PanelBackgroundBrush" };
        var unresolved = FindUnresolvedStaticResources(mockXaml, globalKeys);

        unresolved.Should().ContainSingle()
            .Which.Key.Should().Be("NonExistentBrush");
    }

    [Fact]
    public void StaticResource_parser_handles_whitespace_quotes_and_syntax_variations()
    {
        const string mockXaml = @"
<Grid>
    <Border Background=""{StaticResource DirectBrush}"" />
    <Border Background=""{StaticResource ResourceKey=ExplicitKeyBrush}"" />
    <Border Background=""{StaticResource ResourceKey = SpacedKeyBrush}"" />
    <Border Background=""{StaticResource ResourceKey=""DoubleQuotedBrush""}"" />
    <Border Background=""{StaticResource ResourceKey='SingleQuotedBrush'}"" />
    <Border Background=""{StaticResource ""QuotedBrush""}"" />
    <Border Background=""{StaticResource 'SingleQuotedDirectBrush'}"" />
    <TextBlock Text=""{Binding Value, Converter={StaticResource BoolToVis}}"" />
</Grid>";

        var globalKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "DirectBrush",
            "ExplicitKeyBrush",
            "SpacedKeyBrush",
            "DoubleQuotedBrush",
            "SingleQuotedBrush",
            "QuotedBrush",
            "SingleQuotedDirectBrush"
        };

        var unresolved = FindUnresolvedStaticResources(mockXaml, globalKeys);
        unresolved.Should().BeEmpty("All syntax variations of StaticResource must parse and resolve");
    }

    [Fact]
    public void Comments_do_not_produce_false_positives_or_false_negatives()
    {
        // 1. Commented-out reference to non-existent resource should NOT cause false positive failure.
        // 2. Commented-out resource definition must NOT satisfy a real reference (avoids false negative).
        const string mockXaml = @"
<Grid>
    <!-- <Border Background=""{StaticResource CommentedOutMissingBrush}"" /> -->
    <!-- <SolidColorBrush x:Key=""CommentedOutDefinition"" Color=""Red"" /> -->
    <Border Background=""{StaticResource CommentedOutDefinition}"" />
</Grid>";

        var globalKeys = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = FindUnresolvedStaticResources(mockXaml, globalKeys);

        // Only the real reference to CommentedOutDefinition should be flagged as unresolved.
        unresolved.Should().ContainSingle()
            .Which.Key.Should().Be("CommentedOutDefinition");
    }

    [Fact]
    public void MainWindow_contains_restored_AI_Studio_button_with_valid_binding()
    {
        string appDirectory = ResolveAppProjectDirectory();
        string mainWindowPath = Path.Combine(appDirectory, "Views", "MainWindow.xaml");
        File.Exists(mainWindowPath).Should().BeTrue();

        string xamlContent = File.ReadAllText(mainWindowPath);

        // Verify button presence and attributes
        xamlContent.Should().Contain("Content=\"AI STUDIO\"");
        xamlContent.Should().Contain("Command=\"{Binding OpenAiVisionInspectorCommand}\"");
        xamlContent.Should().Contain("Style=\"{StaticResource ToolbarButtonStyle}\"");

        // Verify backing RelayCommand exists on MainWorkstationViewModel
        var vmType = typeof(BreadboardAOI.App.ViewModels.MainWorkstationViewModel);
        var prop = vmType.GetProperty("OpenAiVisionInspectorCommand");
        prop.Should().NotBeNull("OpenAiVisionInspectorCommand must exist on MainWorkstationViewModel");
    }

    [Fact]
    public void MainWindow_contains_restored_Digital_AVOmeter_with_all_controls_and_bindings()
    {
        string appDirectory = ResolveAppProjectDirectory();
        string mainWindowPath = Path.Combine(appDirectory, "Views", "MainWindow.xaml");
        File.Exists(mainWindowPath).Should().BeTrue();

        string xamlContent = File.ReadAllText(mainWindowPath);

        // 1. Header & Active switch
        xamlContent.Should().Contain("Text=\"DIGITAL AVOmeter\"");
        xamlContent.Should().Contain("Binding IsAvometerActive");

        // 2. LCD Readout Card & Bargraph
        xamlContent.Should().Contain("Text=\"{Binding AvometerDisplayValue}\"");
        xamlContent.Should().Contain("Text=\"{Binding AvometerUnit}\"");
        xamlContent.Should().Contain("Text=\"{Binding AvometerSecondaryReading}\"");
        xamlContent.Should().Contain("Text=\"{Binding AvometerStatus}\"");
        xamlContent.Should().Contain("Value=\"{Binding AvometerBarPercent, Mode=OneWay}\"");

        // 3. Mode buttons
        xamlContent.Should().Contain("CommandParameter=\"V_DC\"");
        xamlContent.Should().Contain("CommandParameter=\"OHMS\"");
        xamlContent.Should().Contain("CommandParameter=\"CONT\"");
        xamlContent.Should().Contain("CommandParameter=\"I_MA\"");
        xamlContent.Should().Contain("Command=\"{Binding SetAvometerModeCommand}\"");

        // 4. Probe Inputs
        xamlContent.Should().Contain("Text=\"{Binding AvometerBlackHole,");
        xamlContent.Should().Contain("Text=\"{Binding AvometerRedHole,");

        // 5. Quick Probe Action Buttons
        xamlContent.Should().Contain("Command=\"{Binding MeasurePowerRailsCommand}\"");
        xamlContent.Should().Contain("Command=\"{Binding AttachAvometerToSelectedPartCommand}\"");
        xamlContent.Should().Contain("Command=\"{Binding SwapAvometerProbesCommand}\"");
        xamlContent.Should().Contain("Command=\"{Binding ClearAvometerProbesCommand}\"");

        // 6. Verify ViewModel properties and commands exist via reflection
        var vmType = typeof(BreadboardAOI.App.ViewModels.MainWorkstationViewModel);
        string[] requiredProperties =
        [
            "IsAvometerActive",
            "AvometerDisplayValue",
            "AvometerUnit",
            "AvometerSecondaryReading",
            "AvometerStatus",
            "AvometerBarPercent",
            "AvometerBlackHole",
            "AvometerRedHole",
            "SetAvometerModeCommand",
            "MeasurePowerRailsCommand",
            "AttachAvometerToSelectedPartCommand",
            "SwapAvometerProbesCommand",
            "ClearAvometerProbesCommand"
        ];

        foreach (string propName in requiredProperties)
        {
            var prop = vmType.GetProperty(propName);
            prop.Should().NotBeNull($"Property or Command '{propName}' must exist on MainWorkstationViewModel");
        }
    }

    [Fact]
    public void MainWindow_has_clean_formatting_without_double_blank_lines()
    {
        string appDirectory = ResolveAppProjectDirectory();
        string mainWindowPath = Path.Combine(appDirectory, "Views", "MainWindow.xaml");
        string[] lines = File.ReadAllLines(mainWindowPath);

        int blankLines = lines.Count(l => string.IsNullOrWhiteSpace(l));
        double blankRatio = (double)blankLines / lines.Length;

        // Clean XAML typically has 5-15% blank lines between sections, never 40-50%
        blankRatio.Should().BeLessThan(0.20,
            $"MainWindow.xaml has {blankLines} blank lines out of {lines.Length} ({blankRatio:P1}), indicating corrupted double newlines.");
    }

    public static List<(string Key, int LineNumber)> FindUnresolvedStaticResources(
        string xamlContent,
        ISet<string> globalKeys,
        ISet<string>? knownExemptions = null)
    {
        string sanitizedContent = StripCommentsPreservingLineBreaks(xamlContent);
        var localKeys = ExtractDefinedKeys(sanitizedContent);
        var exemptions = knownExemptions ?? KnownExemptions;

        var unresolved = new List<(string Key, int LineNumber)>();
        var matches = StaticResourceRegex.Matches(sanitizedContent);

        foreach (Match match in matches)
        {
            string key = match.Groups["key"].Value;

            if (exemptions.Contains(key))
                continue;

            if (globalKeys.Contains(key) || localKeys.Contains(key))
                continue;

            int lineNumber = GetLineNumber(sanitizedContent, match.Index);
            unresolved.Add((key, lineNumber));
        }

        return unresolved;
    }

    public static string StripCommentsPreservingLineBreaks(string content)
    {
        return CommentRegex.Replace(content, m =>
        {
            char[] chars = m.Value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '\r' && chars[i] != '\n')
                {
                    chars[i] = ' ';
                }
            }
            return new string(chars);
        });
    }

    public static HashSet<string> ExtractDefinedKeys(string sanitizedContent)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var matches = ResourceKeyDefinitionRegex.Matches(sanitizedContent);
        foreach (Match m in matches)
        {
            keys.Add(m.Groups["key"].Value);
        }
        return keys;
    }

    public static HashSet<string> ExtractGlobalApplicationKeys(string appDirectory)
    {
        var globalKeys = new HashSet<string>(StringComparer.Ordinal);

        string themePath = Path.Combine(appDirectory, "Themes", "SpectrumDarkTheme.xaml");
        if (File.Exists(themePath))
        {
            string themeContent = StripCommentsPreservingLineBreaks(File.ReadAllText(themePath));
            foreach (string k in ExtractDefinedKeys(themeContent))
            {
                globalKeys.Add(k);
            }
        }

        string appXamlPath = Path.Combine(appDirectory, "App.xaml");
        if (File.Exists(appXamlPath))
        {
            string appContent = StripCommentsPreservingLineBreaks(File.ReadAllText(appXamlPath));
            foreach (string k in ExtractDefinedKeys(appContent))
            {
                globalKeys.Add(k);
            }
        }

        return globalKeys;
    }

    public static int GetLineNumber(string content, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
                line++;
        }
        return line;
    }

    public static string ResolveAppProjectDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "src", "5_BreadboardAOI.App");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            candidate = Path.Combine(current.FullName, "5_BreadboardAOI.App");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            current = current.Parent;
        }

        current = new(Directory.GetCurrentDirectory());
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "src", "5_BreadboardAOI.App");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            candidate = Path.Combine(current.FullName, "5_BreadboardAOI.App");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/5_BreadboardAOI.App from test base directory.");
    }
}
