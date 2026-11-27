// ail-derived-from: ModelSetup@0a39980cee3a6a8b
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class ModelSetupTests(HeadlessFixture fixture)
{
    private const string MissingTitle = "EmbeddingGemma 2 model not installed";
    private const string MissingBody =
        "This build of Valafind comes without the model and does not download it by itself. Install it once by running this command in a terminal:";
    private const string InstallsInto = "It installs into:";
    private const string CheckAgain = "Check again";
    private const string CopyCommand = "Copy command";
    private const string ModelUnavailable = "Model unavailable";
    private const string Welcome = "Search your files by meaning";

    [Fact(DisplayName = "without the model the launcher shows the command that installs it")]
    public Task WithoutTheModelTheLauncherShowsTheCommandThatInstallsIt() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();
        var window = app.LauncherWindow!;

        await WaitUntilAsync(() => AppHarness.Texts(window).Any(t => t.Contains(MissingTitle)), TimeSpan.FromSeconds(10));
        var texts = AppHarness.Texts(window).ToList();

        Assert.Contains(texts, t => t.Contains(MissingTitle));
        Assert.Contains(texts, t => t.Contains(MissingBody));

        var commandIndex = texts.FindIndex(t => t.TrimStart().StartsWith("bash", StringComparison.Ordinal) && t.Contains("fetch-model.sh"));
        Assert.True(commandIndex >= 0, "no command starting with \"bash\" that names fetch-model.sh; texts: " + string.Join(" | ", texts));

        var intoIndex = texts.FindIndex(t => t.Contains(InstallsInto));
        Assert.True(intoIndex >= 0, "no \"It installs into:\"");

        var modelFolder = Path.Combine(app.Data, "models", "embeddinggemma-2");
        var folderIndex = texts.FindIndex(intoIndex, t => t.Contains(modelFolder));
        Assert.True(folderIndex >= intoIndex, $"the folder {modelFolder} is not shown under \"It installs into:\"; texts: " + string.Join(" | ", texts));

        var buttons = AppHarness.Find<Button>(window).ToList();
        var copyButtons = buttons.Where(b => Labels(b).Any(l => l.StartsWith("Copy", StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.Contains(copyButtons, b => Labels(b).Contains(CopyCommand));
        Assert.True(copyButtons.Count >= 2, "expected a copy button for the command and one for the folder");

        Assert.Contains(buttons, b => Labels(b).Contains(CheckAgain));

        Assert.Contains(texts, t => t.Contains(ModelUnavailable));

        AppHarness.SaveScreen(window, "without-the-model-the-launcher-shows-the-command-that-instal.png");
    }, new AppOptions { WithoutModel = true });

    [Fact(DisplayName = "the model is noticed without a restart")]
    public Task TheModelIsNoticedWithoutARestart() => AppHarness.RunAsync(fixture, async app =>
    {
        var source = Environment.GetEnvironmentVariable("VALAFIND_TEST_MODEL_DIR");
        Assert.False(string.IsNullOrWhiteSpace(source), "VALAFIND_TEST_MODEL_DIR is not set");
        Assert.True(Directory.Exists(source), $"the test model folder {source} does not exist");

        await app.StartAsync();
        var window = app.LauncherWindow!;
        await WaitUntilAsync(() => AppHarness.Texts(window).Any(t => t.Contains(MissingTitle)), TimeSpan.FromSeconds(10));
        Assert.Contains(AppHarness.Texts(window), t => t.Contains(MissingTitle));

        var modelsFolder = Path.Combine(app.Data, "models");
        var modelFolder = Path.Combine(modelsFolder, "embeddinggemma-2");
        Directory.CreateDirectory(modelsFolder);
        var staging = Path.Combine(modelsFolder, ".staging-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(source!, staging);
        Directory.Move(staging, modelFolder);

        var found = await WaitUntilAsync(() => AppHarness.Texts(window).Any(t => t.Contains(Welcome)), TimeSpan.FromSeconds(15));
        var texts = AppHarness.Texts(window).ToList();
        Assert.True(found, "the launcher did not show \"Search your files by meaning\"; texts: " + string.Join(" | ", texts));
        Assert.DoesNotContain(texts, t => t.Contains(MissingTitle));

        AppHarness.SaveScreen(window, "the-model-is-noticed-without-a-restart.png");
    }, new AppOptions { WithoutModel = true });

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            if (watch.Elapsed > timeout) return false;
            await Task.Delay(100);
        }
    }

    private static IReadOnlyList<string> Labels(Button button)
    {
        var labels = new List<string>();
        if (button.Content is string s) labels.Add(s.Trim());
        var name = AutomationProperties.GetName(button);
        if (!string.IsNullOrWhiteSpace(name)) labels.Add(name.Trim());
        if (ToolTip.GetTip(button) is string tip && !string.IsNullOrWhiteSpace(tip)) labels.Add(tip.Trim());
        foreach (var block in button.GetLogicalDescendants().OfType<TextBlock>())
        {
            if (!string.IsNullOrWhiteSpace(block.Text)) labels.Add(block.Text!.Trim());
        }
        return labels;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
