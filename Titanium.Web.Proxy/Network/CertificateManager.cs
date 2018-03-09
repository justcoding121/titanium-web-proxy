using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// Certificate Engine option
    /// </summary>
    public enum CertificateEngine
    {
        /// <summary>
        /// Uses Windows Certification Generation API
        /// </summary>
        DefaultWindows = 0,

        /// <summary>
        /// Uses BouncyCastle 3rd party library
        /// </summary>
        BouncyCastle = 1
    }

    /// <summary>
    /// A class to manage SSL certificates used by this proxy server
    /// </summary>
    public sealed class CertificateManager : IDisposable
    {

        private const string defaultRootCertificateIssuer = "Titanium";

        private const string defaultRootRootCertificateName = "Titanium Root Certificate Authority";

        private CertificateEngine engine;

        private ICertificateMaker certEngine;

        private string issuer;

        private string rootCertificateName;

        private bool pfxFileExists = false;

        private bool clearCertificates { get; set; }

        private X509Certificate2 rootCertificate;

        /// <summary>
        /// Cache dictionary
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedCertificate> certificateCache;
        private readonly ConcurrentDictionary<string, Task<X509Certificate2>> pendingCertificateCreationTasks;

        private readonly Action<Exception> exceptionFunc;

        /// <summary>
        /// Is the root certificate used by this proxy is valid?
        /// </summary>
        internal bool CertValidated => RootCertificate != null;

        /// <summary>
        /// Trust the RootCertificate used by this proxy server
        /// Note that this do not make the client trust the certificate!
        /// This would import the root certificate to the certificate store of machine that runs this proxy server
        /// </summary>
        internal bool TrustRoot { get; set; } = false;

        /// <summary>
        /// Needs elevated permission. Works only on Windows.
        /// <para>Puts the certificate to the local machine's certificate store.</para>
        /// <para>Certutil.exe is a command-line program that is installed as part of Certificate Services</para>
        /// </summary>
        internal bool TrustRootAsAdministrator { get; set; } = false;

        /// <summary>
        /// Select Certificate Engine 
        /// Optionally set to BouncyCastle
        /// Mono only support BouncyCastle and it is the default
        /// </summary>
        public CertificateEngine CertificateEngine
        {
            get => engine;
            set
            {
                //For Mono (or Non-Windows) only Bouncy Castle is supported
                if (!RunTime.IsWindows || RunTime.IsRunningOnMono)
                {
                    value = CertificateEngine.BouncyCastle;
                }

                if (value != engine)
                {
                    certEngine = null;
                    engine = value;
                }

                if (certEngine == null)
                {
                    certEngine = engine == CertificateEngine.BouncyCastle ? (ICertificateMaker)new BCCertificateMaker(exceptionFunc) : new WinCertificateMaker(exceptionFunc);
                }
            }
        }

        /// <summary>
        /// Password of the Root certificate file
        /// <para>Set a password for the .pfx file</para>
        /// </summary>
        public string PfxPassword { get; set; } = string.Empty;

        /// <summary>
        /// Name(path) of the Root certificate file
        /// <para>Set the name(path) of the .pfx file. If it is string.Empty Root certificate file will be named as "rootCert.pfx" (and will be saved in proxy dll directory)</para>
        /// </summary>
        public string PfxFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Name of the root certificate issuer 
        /// (This is valid only when RootCertificate property is not set)
        /// </summary>
        public string RootCertificateIssuerName
        {
            get => issuer ?? defaultRootCertificateIssuer;
            set
            {
                issuer = value;
            }
        }

        /// <summary>
        /// Name of the root certificate
        /// (This is valid only when RootCertificate property is not set)
        /// If no certificate is provided then a default Root Certificate will be created and used
        /// The provided root certificate will be stored in proxy exe directory with the private key
        /// Root certificate file will be named as "rootCert.pfx"
        /// </summary>
        public string RootCertificateName
        {
            get => rootCertificateName ?? defaultRootRootCertificateName;
            set
            {
                rootCertificateName = value;
            }
        }

        /// <summary>
        /// The root certificate
        /// </summary>
        public X509Certificate2 RootCertificate
        {
            get => rootCertificate;
            set
            {
                ClearRootCertificate();
                rootCertificate = value;
            }
        }

        /// <summary>
        /// Save all fake certificates in folder "crts"(will be created in proxy dll directory)
        /// <para>for can load the certificate and not make new certificate every time </para>
        /// </summary>
        public bool SaveFakeCertificates { get; set; } = false;

        /// <summary>
        /// Overwrite Root certificate file
        /// <para>true : replace an existing .pfx file if password is incorect or if RootCertificate = null</para>
        /// </summary>
        public bool OverwritePfxFile { get; set; } = true;

        /// <summary>
        /// Minutes certificates should be kept in cache when not used
        /// </summary>
        public int CertificateCacheTimeOutMinutes { get; set; } = 60;

        /// <summary>
        /// Adjust behaviour when certificates are saved to filesystem
        /// </summary>
        public X509KeyStorageFlags StorageFlag { get; set; } = X509KeyStorageFlags.Exportable;


        internal CertificateManager(Action<Exception> exceptionFunc)
        {
            this.exceptionFunc = exceptionFunc;
            if(RunTime.IsWindows)
            {
                //this is faster in Windows based on tests (see unit test project CertificateManagerTests.cs)
                CertificateEngine = CertificateEngine.DefaultWindows;
            }
            else
            {
                CertificateEngine = CertificateEngine.BouncyCastle;
            }

            certificateCache = new ConcurrentDictionary<string, CachedCertificate>();
            pendingCertificateCreationTasks = new ConcurrentDictionary<string, Task<X509Certificate2>>();
        }


        private string GetRootCertificateDirectory()
        {
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;

            // dynamically loaded assemblies returns string.Empty location
            if (assemblyLocation == string.Empty)
            {
                assemblyLocation = Assembly.GetEntryAssembly().Location;
            }

            string path = Path.GetDirectoryName(assemblyLocation);
            if (null == path)
                throw new NullReferenceException();

            return path;
        }

        private string GetCertificatePath()
        {
            string path = GetRootCertificateDirectory();

            string certPath = Path.Combine(path, "crts");
            if (!Directory.Exists(certPath))
            {
                Directory.CreateDirectory(certPath);
            }

            return certPath;
        }

        private string GetRootCertificatePath()
        {
            string path = GetRootCertificateDirectory();

            string fileName = PfxFilePath;
            if (fileName == string.Empty)
            {
                fileName = Path.Combine(path, "rootCert.pfx");
                StorageFlag = X509KeyStorageFlags.Exportable;
            }

            return fileName;
        }

        private bool FindRootCertificate(StoreLocation storeLocation)
        {
            string value = $"{RootCertificate.Issuer}";
            return FindCertificates(StoreName.Root, storeLocation, value).Count > 0;
        }

        private X509Certificate2Collection FindCertificates(StoreName storeName, StoreLocation storeLocation, string findValue)
        {
            var x509Store = new X509Store(storeName, storeLocation);
            try
            {
                x509Store.Open(OpenFlags.OpenExistingOnly);
                return x509Store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, findValue, false);
            }
            finally
            {
                x509Store.Close();
            }
        }

        private X509Certificate2 MakeCertificate(string certificateName, bool isRootCertificate)
        {
            if (!isRootCertificate && RootCertificate == null)
            {
                CreateRootCertificate();
            }

            return certEngine.MakeCertificate(certificateName, isRootCertificate, RootCertificate);
        }

        /// <summary>
        /// Make current machine trust the Root Certificate used by this proxy
        /// </summary>
        /// <param name="storeLocation"></param>
        /// <returns></returns>
        private void TrustRootCertificate(StoreLocation storeLocation)
        {
            if (RootCertificate == null)
            {
                exceptionFunc(
                    new Exception("Could not set root certificate"
                                  + " as system proxy since it is null or empty."));

                return;
            }

            var x509RootStore = new X509Store(StoreName.Root, storeLocation);
            var x509PersonalStore = new X509Store(StoreName.My, storeLocation);

            try
            {
                x509RootStore.Open(OpenFlags.ReadWrite);
                x509PersonalStore.Open(OpenFlags.ReadWrite);

                x509RootStore.Add(RootCertificate);
                x509PersonalStore.Add(RootCertificate);
            }
            catch (Exception e)
            {
                exceptionFunc(
                    new Exception("Failed to make system trust root certificate "
                                  + $" for {storeLocation} store location. You may need admin rights.", e));
            }
            finally
            {
                x509RootStore.Close();
                x509PersonalStore.Close();
            }
        }

        /// <summary>
        /// Remove the Root Certificate trust
        /// </summary>
        /// <param name="storeLocation"></param>
        /// <returns></returns>
        private void RemoveTrustedRootCertificate(StoreLocation storeLocation)
        {
            if (RootCertificate == null)
            {
                exceptionFunc(
                    new Exception("Could not set root certificate"
                                  + " as system proxy since it is null or empty."));

                return;
            }

            var x509RootStore = new X509Store(StoreName.Root, storeLocation);
            var x509PersonalStore = new X509Store(StoreName.My, storeLocation);

            try
            {
                x509RootStore.Open(OpenFlags.ReadWrite);
                x509PersonalStore.Open(OpenFlags.ReadWrite);

                x509RootStore.Remove(RootCertificate);
                x509PersonalStore.Remove(RootCertificate);
            }
            catch (Exception e)
            {
                exceptionFunc(
                    new Exception("Failed to remove root certificate trust "
                                  + $" for {storeLocation} store location. You may need admin rights.", e));
            }
            finally
            {
                x509RootStore.Close();
                x509PersonalStore.Close();
            }
        }

        /// <summary>
        ///  Create an SSL certificate
        /// </summary>
        /// <param name="certificateName"></param>
        /// <param name="isRootCertificate"></param>
        /// <returns></returns>
        internal X509Certificate2 CreateCertificate(string certificateName, bool isRootCertificate)
        {

            X509Certificate2 certificate = null;
            try
            {
                if (!isRootCertificate && SaveFakeCertificates)
                {
                    string path = GetCertificatePath();
                    string subjectName = BCCertificateMaker.CNRemoverRegex.Replace(certificateName, string.Empty);
                    subjectName = subjectName.Replace("*", "$x$");
                    subjectName = Path.Combine(path, subjectName + ".pfx");

                    if (!File.Exists(subjectName))
                    {
                        certificate = MakeCertificate(certificateName, isRootCertificate);
                        File.WriteAllBytes(subjectName, certificate.Export(X509ContentType.Pkcs12));
                    }
                    else
                    {
                        try
                        {
                            certificate = new X509Certificate2(subjectName, string.Empty, StorageFlag);
                        }
                        catch /* (Exception e)*/
                        {
                            certificate = MakeCertificate(certificateName, isRootCertificate);
                        }
                    }
                }
                else
                {
                    certificate = MakeCertificate(certificateName, isRootCertificate);
                }
            }
            catch (Exception e)
            {
                exceptionFunc(e);
            }

            return certificate;
        }

        /// <summary>
        /// Create an SSL certificate async
        /// </summary>
        /// <param name="certificateName"></param>
        /// <returns></returns>
        internal async Task<X509Certificate2> CreateCertificateAsync(string certificateName)
        {
            //check in cache first
            CachedCertificate cached;
            if (certificateCache.TryGetValue(certificateName, out cached))
            {
                cached.LastAccess = DateTime.Now;
                return cached.Certificate;
            }

            //handle burst requests with same certificate name
            //by checking for existing task for same certificate name
            Task<X509Certificate2> task;
            if (pendingCertificateCreationTasks.TryGetValue(certificateName, out task))
            {
                //certificate already added to cache
                //just return the result here
                return await task;
            }

            //run certificate creation task & add it to pending tasks
            task = Task.Run(() =>
            {
                var result = CreateCertificate(certificateName, false);
                if (result != null)
                {
                    //this is ConcurrentDictionary
                    //if key exists it will silently handle; no need for locking
                    certificateCache.TryAdd(certificateName, new CachedCertificate
                    {
                        Certificate = result
                    });

                }
                return result;
            });
            pendingCertificateCreationTasks.TryAdd(certificateName, task);

            //cleanup pending tasks & return result
            var certificate = await task;
            pendingCertificateCreationTasks.TryRemove(certificateName, out task);

            return certificate;

        }

        /// <summary>
        /// Stops the certificate cache clear process
        /// </summary>
        internal void StopClearIdleCertificates()
        {
            clearCertificates = false;
        }

        /// <summary>
        /// A method to clear outdated certificates
        /// </summary>
        internal async void ClearIdleCertificates()
        {
            clearCertificates = true;
            while (clearCertificates)
            {
                var cutOff = DateTime.Now.AddMinutes(-1 * CertificateCacheTimeOutMinutes);

                var outdated = certificateCache.Where(x => x.Value.LastAccess < cutOff).ToList();

                CachedCertificate removed;
                foreach (var cache in outdated)
                    certificateCache.TryRemove(cache.Key, out removed);

                //after a minute come back to check for outdated certificates in cache
                await Task.Delay(1000 * 60);
            }
        }

        /// <summary>
        /// Load or Create Certificate : after "Test Is the root certificate used by this proxy is valid?"
        /// <param name="trustRootCertificate">"Make current machine trust the Root Certificate used by this proxy" ==> True or False</param>
        /// </summary>
        public void EnsureRootCertificate(bool trustRootCertificate, bool trustRootCertificateAsAdmin)
        {
            this.TrustRoot = trustRootCertificate;
            this.TrustRootAsAdministrator = trustRootCertificateAsAdmin;
            EnsureRootCertificate();
        }

        /// <summary>
        /// Create/load root if required and ensure that the root certificate is trusted.
        /// </summary>
        public void EnsureRootCertificate()
        {
            if (!CertValidated)
            {
                CreateRootCertificate();

                if (TrustRoot)
                {
                    TrustRootCertificate();
                }
                else
                {
                    RemoveTrustedRootCertificate();
                }

                if (TrustRootAsAdministrator)
                {
                    TrustRootCertificateAsAdmin();
                }
                else
                {
                    RemoveTrustedRootCertificateAsAdmin();
                }
            }
        }

        public void ClearRootCertificate()
        {
            certificateCache.Clear();
            rootCertificate = null;
        }

        public X509Certificate2 LoadRootCertificate()
        {
            string fileName = GetRootCertificatePath();
            pfxFileExists = File.Exists(fileName);
            if (!pfxFileExists)
            {
                return null;
            }

            try
            {
                return new X509Certificate2(fileName, PfxPassword, StorageFlag);
            }
            catch (Exception e)
            {
                exceptionFunc(e);
                return null;
            }
        }

        /// <summary>
        /// Attempts to create a RootCertificate
        /// </summary>
        /// <param name="persistToFile">if set to <c>true</c> try to load/save the certificate from rootCert.pfx.</param>
        /// <returns>
        /// true if succeeded, else false
        /// </returns>
        public bool CreateRootCertificate(bool persistToFile = true)
        {
            if (persistToFile && RootCertificate == null)
            {
                RootCertificate = LoadRootCertificate();
            }

            if (RootCertificate != null)
            {
                return true;
            }

            if (!OverwritePfxFile && pfxFileExists)
            {
                return false;
            }

            try
            {
                RootCertificate = CreateCertificate(RootCertificateName, true);
            }
            catch (Exception e)
            {
                exceptionFunc(e);
            }

            if (persistToFile && RootCertificate != null)
            {
                try
                {
                    try
                    {
                        Directory.Delete(GetCertificatePath(), true);
                    }
                    catch
                    {
                        // ignore
                    }

                    string fileName = GetRootCertificatePath();
                    File.WriteAllBytes(fileName, RootCertificate.Export(X509ContentType.Pkcs12, PfxPassword));
                }
                catch (Exception e)
                {
                    exceptionFunc(e);
                }
            }

            return RootCertificate != null;
        }

        /// <summary>
        /// Manually load a Root certificate file(.pfx file)
        /// </summary> 
        /// <param name="pfxFilePath">Set the name(path) of the .pfx file. If it is string.Empty Root certificate file will be named as "rootCert.pfx" (and will be saved in proxy dll directory)</param>
        /// <param name="password">Set a password for the .pfx file</param>
        /// <param name="overwritePfXFile">true : replace an existing .pfx file if password is incorect or if RootCertificate==null</param>
        /// <param name="storageFlag"></param>
        /// <returns>
        /// true if succeeded, else false
        /// </returns>
        public bool LoadRootCertificate(string pfxFilePath, string password, bool overwritePfXFile = true, X509KeyStorageFlags storageFlag = X509KeyStorageFlags.Exportable)
        {
            PfxFilePath = pfxFilePath;
            PfxPassword = password;
            OverwritePfxFile = overwritePfXFile;
            StorageFlag = storageFlag;

            RootCertificate = LoadRootCertificate();

            return (RootCertificate != null);
        }

        /// <summary>
        /// Trusts the root certificate.
        /// </summary>
        public void TrustRootCertificate()
        {
            //current user
            TrustRootCertificate(StoreLocation.CurrentUser);

            //current system
            TrustRootCertificate(StoreLocation.LocalMachine);
        }

        /// <summary>
        /// Puts the certificate to the local machine's certificate store.
        /// Needs elevated permission. Works only on Windows.
        /// </summary>
        /// <returns></returns>
        public bool TrustRootCertificateAsAdmin()
        {
            if (!RunTime.IsWindows || RunTime.IsRunningOnMono)
            {
                return false;
            }

            string fileName = Path.GetTempFileName();
            File.WriteAllBytes(fileName, RootCertificate.Export(X509ContentType.Pkcs12, PfxPassword));

            var info = new ProcessStartInfo
            {
                FileName = "certutil.exe",
                Arguments = "-importPFX -p \"" + PfxPassword + "\" -f \"" + fileName + "\"",
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = "runas",
                ErrorDialog = false,
            };

            try
            {
                var process = Process.Start(info);
                if (process == null)
                {
                    return false;
                }

                process.WaitForExit();

                File.Delete(fileName);
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Removes the trusted certificates.
        /// </summary>
        public void RemoveTrustedRootCertificate()
        {
            //current user
            RemoveTrustedRootCertificate(StoreLocation.CurrentUser);

            //current system
            RemoveTrustedRootCertificate(StoreLocation.LocalMachine);
        }

        /// <summary>
        /// Removes the trusted certificates from the local machine's certificate store.
        /// Needs elevated permission. Works only on Windows.
        /// </summary>
        /// <returns></returns>
        public bool RemoveTrustedRootCertificateAsAdmin()
        {
            if (!RunTime.IsWindows || RunTime.IsRunningOnMono)
            {
                return false;
            }

            var info = new ProcessStartInfo
            {
                FileName = "certutil.exe",
                Arguments = "-delstore Root \"" + RootCertificateName + "\"",
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = "runas",
                ErrorDialog = false,
            };

            try
            {
                var process = Process.Start(info);
                if (process == null)
                {
                    return false;
                }

                process.WaitForExit();
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether the root certificate is trusted.
        /// </summary>
        public bool IsRootCertificateTrusted()
        {
            return FindRootCertificate(StoreLocation.CurrentUser) || IsRootCertificateMachineTrusted();
        }

        /// <summary>
        /// Determines whether the root certificate is machine trusted.
        /// </summary>
        public bool IsRootCertificateMachineTrusted()
        {
            return FindRootCertificate(StoreLocation.LocalMachine);
        }

     

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
