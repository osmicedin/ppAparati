using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;

namespace PpEvidencija.Tests;

[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfCollection
{
}

public static class WpfTestHost
{
    private static readonly Lazy<Task<Dispatcher>> DispatcherTask = new(StartDispatcher);

    public static async Task Run(Action action)
    {
        var dispatcher = await DispatcherTask.Value;
        await dispatcher.InvokeAsync(action);
    }

    public static async Task RunAsync(Func<Task> action)
    {
        var dispatcher = await DispatcherTask.Value;
        await await dispatcher.InvokeAsync(action);
    }

    private static Task<Dispatcher> StartDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // A plain Application supplies resources without running production startup/login.
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var appSource = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "AppSource.xaml"));
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var root = appSource.Root!;
                var resources = root.Element(presentation + "Application.Resources")!;
                var dictionary = new XElement(
                    presentation + "ResourceDictionary",
                    root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
                    resources.Nodes());
                using var reader = dictionary.CreateReader();
                application.Resources = (ResourceDictionary)XamlReader.Load(reader);

                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                ready.SetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "ppEvidencija WPF tests"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
