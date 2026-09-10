using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class ExportRootCertificateFormatTests
{
    [TestMethod]
    public void IsPemExportPath_OnlyMatchesPemExtension()
    {
        Assert.IsTrue(InterceptionService.IsPemExportPath(@"C:\tmp\ca.pem"));
        Assert.IsTrue(InterceptionService.IsPemExportPath("/tmp/ca.PEM"));
        Assert.IsFalse(InterceptionService.IsPemExportPath(@"C:\tmp\ca.cer"));
        Assert.IsFalse(InterceptionService.IsPemExportPath(@"C:\tmp\ca.crt"));
        Assert.IsFalse(InterceptionService.IsPemExportPath(@"C:\tmp\ca"));
    }

    [TestMethod]
    public void EncodeCertificatePem_WrapsBase64WithHeaders()
    {
        var der = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var pem = InterceptionService.EncodeCertificatePem(der);
        StringAssert.StartsWith(pem, "-----BEGIN CERTIFICATE-----");
        StringAssert.Contains(pem, "-----END CERTIFICATE-----");
        Assert.IsFalse(pem.Contains('\r'));

        var body = pem
            .Replace("-----BEGIN CERTIFICATE-----", "", StringComparison.Ordinal)
            .Replace("-----END CERTIFICATE-----", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
        CollectionAssert.AreEqual(der, Convert.FromBase64String(body));
    }

    [TestMethod]
    public async Task ExportRootCertificate_WritesDerForCer_AndPemForPem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-export-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cer = Path.Combine(dir, "root.cer");
        var pem = Path.Combine(dir, "root.pem");
        try
        {
            using var interception = new InterceptionService { UseInMemoryTrustState = true };
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsNotNull(interception.RootCertificate);

            Assert.AreEqual(cer, interception.ExportRootCertificate(cer));
            Assert.AreEqual(pem, interception.ExportRootCertificate(pem));

            var derBytes = await File.ReadAllBytesAsync(cer);
            using var fromCer = X509CertificateLoader.LoadCertificate(derBytes);
            Assert.AreEqual(interception.RootCertificate!.Thumbprint, fromCer.Thumbprint);

            var pemText = await File.ReadAllTextAsync(pem, Encoding.ASCII);
            StringAssert.StartsWith(pemText, "-----BEGIN CERTIFICATE-----");
            using var fromPem = X509Certificate2.CreateFromPem(pemText);
            Assert.AreEqual(interception.RootCertificate.Thumbprint, fromPem.Thumbprint);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task ExportCaCommand_OffersCerAndPemFilters_AndEncodesByExtension()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-ca-fmt-" + Guid.NewGuid().ToString("N") + ".json");
        var pemPath = Path.Combine(Path.GetTempPath(), "twp-ca-fmt-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            var settings = new SettingsService(settingsPath);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var registry = new SessionRegistry();
            var picker = new ScriptedInspectorPathPicker { SavePath = pemPath };
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                pathPicker: picker);

            vm.BindPort = 0;
            vm.BindAddress = "127.0.0.1";
            await ExecuteAsync(vm.StartCaptureCommand);
            Assert.IsTrue(interception.IsRunning, vm.StatusText);

            await ExecuteAsync(vm.ExportCaCommand);
            StringAssert.Contains(vm.StatusText, "Exported CA");
            Assert.AreEqual(1, picker.SaveCalls);
            Assert.IsNotNull(picker.LastSaveFileTypes);
            Assert.AreEqual(2, picker.LastSaveFileTypes!.Count);
            Assert.AreEqual("Certificate", picker.LastSaveFileTypes[0].Name);
            Assert.AreEqual("*.cer", picker.LastSaveFileTypes[0].Pattern);
            Assert.AreEqual("PEM", picker.LastSaveFileTypes[1].Name);
            Assert.AreEqual("*.pem", picker.LastSaveFileTypes[1].Pattern);
            Assert.IsTrue(File.Exists(pemPath));
            StringAssert.StartsWith(await File.ReadAllTextAsync(pemPath), "-----BEGIN CERTIFICATE-----");
        }
        finally
        {
            try { File.Delete(settingsPath); } catch { /* ignore */ }
            try { File.Delete(pemPath); } catch { /* ignore */ }
        }
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await Task.Delay(250);
    }
}
