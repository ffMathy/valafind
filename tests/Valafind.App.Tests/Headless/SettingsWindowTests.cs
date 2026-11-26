using Avalonia.Controls;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Valafind.Core.Configuration;
using Xunit;

namespace Valafind.App.Tests.Headless;

[Collection(HeadlessUiDefinition.Name)]
public sealed class SettingsWindowTests(HeadlessFixture fixture)
{
    /// <summary>A copy from an app store is updated by the store: its settings offer no update setting or check.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UpdatesAreOfferedOnlyWhenThisCopyUpdatesItself(bool showsUpdates) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = new SettingsViewModel(new ValafindSettings(), SettingsViewModel.DesignHost.Instance) { ShowsUpdates = showsUpdates };
            var window = new SettingsWindow(vm);
            window.Show();
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal(showsUpdates, window.FindControl<Border>("UpdatesItem")!.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
}
