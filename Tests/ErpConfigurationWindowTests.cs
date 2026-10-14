using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PpEvidencija.Models;
using PpEvidencija.Services;
using PpEvidencija.Views;

namespace PpEvidencija.Tests;

[Collection("WPF")]
public sealed class ErpConfigurationWindowTests
{
    [Fact]
    public Task LoginRetainsTheSessionUsedForAuthenticationAndNextLoginReadsNewSettings() => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService();
        var firstSnapshot = service.Session;
        var pendingUser = new TaskCompletionSource<AutentifikovaniKorisnik?>();
        ErpConnectionSnapshot? authenticatingSession = null;
        var first = new LoginWindow(service, (snapshot, _, _, _) =>
        {
            authenticatingSession = snapshot;
            return pendingUser.Task;
        });
        var second = new LoginWindow(service, (_, _, _, _) => Task.FromResult<AutentifikovaniKorisnik?>(User));
        try
        {
            FillLogin(first);
            var login = first.TryLoginAsync();
            service.Session = Snapshot(2);
            pendingUser.SetResult(User);

            Assert.True(await login);
            Assert.Same(firstSnapshot, authenticatingSession);
            Assert.Same(firstSnapshot, first.ErpSession);
            Assert.Same(User, first.PrijavljeniKorisnik);
            Assert.Empty(Control<PasswordBox>(first, "txtLozinka").Password);

            FillLogin(second);
            Assert.True(await second.TryLoginAsync());
            Assert.Same(service.Session, second.ErpSession);
            Assert.NotSame(first.ErpSession, second.ErpSession);
            Assert.Equal(2, service.OpenCalls);
        }
        finally { first.Close(); second.Close(); }
    });

    [Fact]
    public Task FailedAuthenticationDoesNotExposeAUserOrSession() => WpfTestHost.RunAsync(async () =>
    {
        var window = new LoginWindow(new FakeConfigurationService(), (_, _, _, _) => Task.FromResult<AutentifikovaniKorisnik?>(null));
        try
        {
            FillLogin(window);
            Assert.False(await window.TryLoginAsync());
            Assert.Null(window.PrijavljeniKorisnik);
            Assert.Null(window.ErpSession);
            Assert.Empty(Control<PasswordBox>(window, "txtLozinka").Password);
            Assert.Contains("nije aktivan", Control<TextBlock>(window, "txtStatus").Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UnconfiguredOrUnavailableErpLeavesConfigurationAvailable(bool unconfigured) => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService
        {
            Open = _ => Task.FromException<ErpConnectionSnapshot>(unconfigured
                ? new ErpConfigurationException("ERP baza nije konfigurirana. Otvorite konfiguraciju ERP baze.")
                : new InvalidOperationException("password=do-not-display"))
        };
        var authCalled = false;
        var window = new LoginWindow(service, (_, _, _, _) =>
        {
            authCalled = true;
            return Task.FromResult<AutentifikovaniKorisnik?>(User);
        });
        try
        {
            FillLogin(window);
            Assert.True(Control<Button>(window, "btnErpKonfiguracija").IsEnabled);
            Assert.False(await window.TryLoginAsync());
            Assert.True(Control<Button>(window, "btnErpKonfiguracija").IsEnabled);
            Assert.False(authCalled);
            Assert.Contains("ERP", Control<TextBlock>(window, "txtStatus").Text);
            Assert.DoesNotContain("do-not-display", Control<TextBlock>(window, "txtStatus").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ConfigurationButtonDirectlyOpensSettingsBeforeLoginOrErpSetup() => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService
        {
            Open = _ => Task.FromException<ErpConnectionSnapshot>(new InvalidOperationException("ERP is unavailable"))
        };
        ErpConfigurationWindow? opened = null;
        var window = new LoginWindow(service, (_, _, _, _) => throw new InvalidOperationException("Login must not be attempted"),
            (_, dialog) => { opened = dialog; return null; });
        try
        {
            Control<Button>(window, "btnErpKonfiguracija").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(opened);
            Assert.Null(window.PrijavljeniKorisnik);
            Assert.Equal(0, service.OpenCalls);
            Assert.True(await opened.LoadAsync());
            Assert.Equal(1, service.MetadataCalls);
            Assert.True(Control<Button>(opened, "btnSacuvaj").IsEnabled);
            Assert.Null(opened.FindName("txtAdminLozinka"));
        }
        finally { opened?.Close(); window.Close(); }
    });

    [Fact]
    public Task ClosingLoginCancelsPendingWorkAndDoesNotAuthenticateAfterItCompletes() => WpfTestHost.RunAsync(async () =>
    {
        var pending = new TaskCompletionSource<ErpConnectionSnapshot>();
        var service = new FakeConfigurationService { Open = _ => pending.Task };
        var authCalled = false;
        var window = new LoginWindow(service, (_, _, _, _) =>
        {
            authCalled = true;
            return Task.FromResult<AutentifikovaniKorisnik?>(User);
        });
        FillLogin(window);
        var login = window.TryLoginAsync();
        Assert.False(await window.TryLoginAsync());
        Assert.Equal(1, service.OpenCalls);
        window.Close();
        Assert.True(service.LastToken.IsCancellationRequested);
        pending.SetResult(service.Session);
        Assert.False(await login);
        Assert.False(authCalled);
        Assert.Null(window.ErpSession);
    });

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "   ")]
    [InlineData(false, "\t\r\n")]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    [InlineData(true, "\t\r\n")]
    public Task EmptyOrWhitespacePasswordIsRejectedBeforeTestOrSaveForNewAndExistingSettings(bool existing, string password) => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService();
        var window = await ConfigurationWindow(service, existing ? Metadata() : null);
        try
        {
            FillConfiguration(window);
            Control<PasswordBox>(window, "txtPassword").Password = password;
            Assert.False(await window.TestAsync());
            Assert.False(await window.SaveAsync());
            Assert.Equal(0, service.TestCalls);
            Assert.Equal(0, service.SaveCalls);
            Assert.Contains("SQL lozinku", Control<TextBlock>(window, "txtStatus").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ExistingSettingsNeverPrefillPasswordAndSaveExplicitReplacementWithOriginalVersion() => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService();
        var metadata = Metadata();
        var originalVersion = metadata.Version.ToArray();
        var window = await ConfigurationWindow(service, metadata);
        try
        {
            Assert.Empty(Control<PasswordBox>(window, "txtPassword").Password);
            Assert.Equal(metadata.ServerName, Control<TextBox>(window, "txtServer").Text);
            metadata.Version[0] = 90;
            Assert.False(await window.TestAsync());
            Assert.False(await window.SaveAsync());
            Assert.Equal(0, service.TestCalls);
            Assert.Equal(0, service.SaveCalls);
            Control<PasswordBox>(window, "txtPassword").Password = "explicit SQL password";
            Assert.True(await window.SaveAsync());
            Assert.Equal("explicit SQL password", service.LastInput!.Password);
            Assert.Equal(originalVersion, service.LastVersion);
            Assert.Equal(1, service.SaveCalls);
            Assert.Equal(0, service.TestCalls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ClearingPasswordAfterSuccessfulTestPreventsSave() => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService();
        var window = await ConfigurationWindow(service, Metadata());
        try
        {
            Control<PasswordBox>(window, "txtPassword").Password = "tested SQL password";
            Assert.True(await window.TestAsync());
            Control<PasswordBox>(window, "txtPassword").Clear();
            Assert.False(await window.SaveAsync());
            Assert.Equal(1, service.TestCalls);
            Assert.Equal(0, service.SaveCalls);
            Assert.Contains("SQL lozinku", Control<TextBlock>(window, "txtStatus").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TestIsNonSavingAndSavePassesCurrentFieldsIncludingUntrimmedPassword() => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService();
        var window = await ConfigurationWindow(service);
        try
        {
            FillConfiguration(window);
            Control<PasswordBox>(window, "txtPassword").Password = "  SQL secret  ";
            Assert.True(await window.TestAsync());
            Assert.Equal(1, service.TestCalls);
            Assert.Equal(0, service.SaveCalls);
            Assert.Equal("Konekcija je uspješna. Kliknite Sačuvaj za spremanje postavki.",
                Control<TextBlock>(window, "txtStatus").Text);
            Control<TextBox>(window, "txtDatabase").Text = "changed_after_test";

            Assert.True(await window.SaveAsync());
            Assert.Equal(1, service.SaveCalls);
            Assert.Equal("changed_after_test", service.LastInput!.DatabaseName);
            Assert.Equal("  SQL secret  ", service.LastInput.Password);
            Assert.Null(service.LastVersion);
            Assert.Empty(Control<PasswordBox>(window, "txtPassword").Password);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedTestOrSavePreservesInputAndHidesRawErrors(bool save) => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService
        {
            Test = (_, _) => Task.FromException(new InvalidOperationException("password=hidden-secret")),
            Save = (_, _, _) => Task.FromException(new InvalidOperationException("password=hidden-secret"))
        };
        var window = await ConfigurationWindow(service, Metadata());
        try
        {
            Control<PasswordBox>(window, "txtPassword").Password = "new password";
            Assert.False(await (save ? window.SaveAsync() : window.TestAsync()));
            Assert.Equal("new password", Control<PasswordBox>(window, "txtPassword").Password);
            Assert.True(Control<Button>(window, "btnTest").IsEnabled);
            Assert.True(Control<Button>(window, "btnSacuvaj").IsEnabled);
            Assert.DoesNotContain("hidden-secret", Control<TextBlock>(window, "txtStatus").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ConcurrentActionsAreIgnoredAndClosingCancelsPendingCommand() => WpfTestHost.RunAsync(async () =>
    {
        var pending = new TaskCompletionSource();
        var service = new FakeConfigurationService { Test = (_, _) => pending.Task };
        var window = await ConfigurationWindow(service, Metadata());
        Control<PasswordBox>(window, "txtPassword").Password = "new SQL password";
        var testing = window.TestAsync();
        Assert.False(Control<Button>(window, "btnTest").IsEnabled);
        Assert.False(Control<Button>(window, "btnSacuvaj").IsEnabled);
        Assert.False(await window.TestAsync());
        Assert.False(await window.SaveAsync());
        Assert.Equal(1, service.TestCalls);
        Assert.Equal(0, service.SaveCalls);

        window.Close();
        Assert.True(service.LastToken.IsCancellationRequested);
        Assert.Empty(Control<PasswordBox>(window, "txtPassword").Password);
        pending.SetResult();
        Assert.False(await testing);
    });

    [Fact]
    public Task ClosingWhileMetadataLoadsCancelsAndDoesNotEnableActions() => WpfTestHost.RunAsync(async () =>
    {
        var pending = new TaskCompletionSource<ErpConfigurationMetadata?>();
        var service = new FakeConfigurationService { LoadMetadata = _ => pending.Task };
        var window = new ErpConfigurationWindow(service);
        var loading = window.LoadAsync();
        Assert.False(await window.LoadAsync());
        Assert.False(await window.SaveAsync());
        Assert.Equal(1, service.MetadataCalls);
        window.Close();
        Assert.True(service.LastToken.IsCancellationRequested);
        pending.SetResult(Metadata());
        Assert.False(await loading);
        Assert.Empty(Control<TextBox>(window, "txtServer").Text);
        Assert.False(Control<Button>(window, "btnSacuvaj").IsEnabled);
    });

    [Fact]
    public Task MetadataFailureCanBeRetriedWithoutOverwritingUnknownSettings() => WpfTestHost.RunAsync(async () =>
    {
        var failed = true;
        var service = new FakeConfigurationService
        {
            LoadMetadata = _ => failed
                ? Task.FromException<ErpConfigurationMetadata?>(new InvalidOperationException("password=hidden-secret"))
                : Task.FromResult<ErpConfigurationMetadata?>(Metadata())
        };
        var window = new ErpConfigurationWindow(service);
        try
        {
            Assert.False(await window.LoadAsync());
            Assert.False(await window.SaveAsync());
            Assert.Equal(0, service.SaveCalls);
            Assert.Equal(Visibility.Visible, Control<Button>(window, "btnPonovoUcitaj").Visibility);
            Assert.DoesNotContain("hidden-secret", Control<TextBlock>(window, "txtStatus").Text);
            failed = false;
            Assert.True(await window.LoadAsync());
            Assert.True(Control<Button>(window, "btnSacuvaj").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Control<Button>(window, "btnPonovoUcitaj").Visibility);
            Assert.Equal("erp_test", Control<TextBox>(window, "txtDatabase").Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("login", 480, 610)]
    [InlineData("configuration", 600, 650)]
    [InlineData("configuration", 520, 430)]
    public Task FormsKeepActionsWithinClientArea(string form, int width, int height) => WpfTestHost.RunAsync(async () =>
    {
        var service = new FakeConfigurationService();
        Window window = form switch
        {
            "login" => new LoginWindow(service),
            _ => await ConfigurationWindow(service, Metadata())
        };
        try
        {
            var root = (FrameworkElement)window.Content;
            if (root is Panel panel) panel.Background = window.Background;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var buttons = form switch
            {
                "login" => new[] { "btnPrijava", "btnErpKonfiguracija" },
                _ => new[] { "btnTest", "btnSacuvaj", "btnOdustani" }
            };
            foreach (var name in buttons)
            {
                var button = Control<Button>(window, name);
                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= width, $"{name}: {bounds}");
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= height, $"{name}: {bounds}");
                Assert.True(button.ActualHeight >= 40);
            }

            var directory = Environment.GetEnvironmentVariable("PPEVIDENCIJA_UI_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"erp-{form}-{width}.png"));
                encoder.Save(output);
            }
        }
        finally { window.Close(); }
    });

    private static readonly AutentifikovaniKorisnik User = new("tester", "tester", "Test", "Korisnik");
    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void FillLogin(LoginWindow window)
    {
        Control<TextBox>(window, "txtKorisnickoIme").Text = "tester";
        Control<PasswordBox>(window, "txtLozinka").Password = "test-password";
    }
    private static void FillConfiguration(ErpConfigurationWindow window)
    {
        Control<TextBox>(window, "txtServer").Text = "erp-server\\erp-instance";
        Control<TextBox>(window, "txtDatabase").Text = "erp_test";
        Control<TextBox>(window, "txtUser").Text = "erp_reader";
    }
    private static async Task<ErpConfigurationWindow> ConfigurationWindow(FakeConfigurationService service, ErpConfigurationMetadata? metadata = null)
    {
        service.Settings = metadata;
        var window = new ErpConfigurationWindow(service);
        Assert.True(await window.LoadAsync());
        return window;
    }
    private static ErpConfigurationMetadata Metadata() => new()
    {
        ServerName = "erp-server\\erp-instance", DatabaseName = "erp_test", UserName = "erp_reader",
        Version = [1, 2, 3, 4, 5, 6, 7, 8], ChangedUtc = DateTimeOffset.UtcNow
    };
    private static ErpConnectionSnapshot Snapshot(byte version) => new(
        "Server=unused-ui-test;Database=erp_test;Integrated Security=true;TrustServerCertificate=true", [version]);

    private sealed class FakeConfigurationService : IErpConfigurationService
    {
        public ErpConnectionSnapshot Session { get; set; } = Snapshot(1);
        public ErpConfigurationMetadata? Settings { get; set; }
        public Func<CancellationToken, Task<ErpConnectionSnapshot>>? Open { get; init; }
        public Func<CancellationToken, Task<ErpConfigurationMetadata?>>? LoadMetadata { get; init; }
        public Func<ErpConfigurationInput, CancellationToken, Task> Test { get; init; } = (_, _) => Task.CompletedTask;
        public Func<ErpConfigurationInput, byte[]?, CancellationToken, Task> Save { get; init; } = (_, _, _) => Task.CompletedTask;
        public int OpenCalls { get; private set; }
        public int MetadataCalls { get; private set; }
        public int TestCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public ErpConfigurationInput? LastInput { get; private set; }
        public byte[]? LastVersion { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<ErpConfigurationMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
        {
            MetadataCalls++;
            LastToken = cancellationToken;
            return LoadMetadata?.Invoke(cancellationToken) ?? Task.FromResult(Settings);
        }
        public Task TestAsync(ErpConfigurationInput input, CancellationToken cancellationToken = default)
        {
            TestCalls++;
            LastInput = input;
            LastToken = cancellationToken;
            return Test(input, cancellationToken);
        }
        public Task SaveAsync(ErpConfigurationInput input, byte[]? expectedVersion, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            LastInput = input;
            LastVersion = expectedVersion;
            LastToken = cancellationToken;
            return Save(input, expectedVersion, cancellationToken);
        }
        public Task<ErpConnectionSnapshot> OpenSessionAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            LastToken = cancellationToken;
            return Open?.Invoke(cancellationToken) ?? Task.FromResult(Session);
        }
    }
}
