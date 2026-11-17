using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace Valafind.App.Tests.Headless;

/// <summary>Avalonia entry point for the headless tests: the real <see cref="App"/> (styles, icons) rendered by Skia.</summary>
public static class HeadlessTestApp
{
    /// <summary>Builder used by <see cref="HeadlessUnitTestSession"/>.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>One headless Avalonia session (UI thread + dispatcher) shared by all UI tests.</summary>
public sealed class HeadlessFixture : IDisposable
{
    /// <summary>The running session; use <see cref="HeadlessUnitTestSession.Dispatch(Action, CancellationToken)"/>.</summary>
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    /// <inheritdoc />
    /// <remarks>Disposing the session can block on its dispatcher (Avalonia 12.1); its thread is a background thread,
    /// so the wait is bounded instead of letting the test run hang at exit.</remarks>
    public void Dispose() => _ = Task.Run(Session.Dispose).Wait(TimeSpan.FromSeconds(2));
}

/// <summary>Serialises UI tests on the shared headless session.</summary>
[CollectionDefinition(Name)]
public sealed class HeadlessUiDefinition : ICollectionFixture<HeadlessFixture>
{
    /// <summary>Collection name.</summary>
    public const string Name = "Headless UI";
}
